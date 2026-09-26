import AppKit

/// All bubbles form one stack against a screen edge, as on Android: drag it and the others
/// trail behind the one you hold; click it and they fan out into a column with the chosen
/// window beside its bubble. Every movement is a spring. Same design as the Windows version.
final class BubbleManager: BubbleViewDelegate {
    // Springs: stiffness in 1/s², damping as a ratio (1 = no overshoot).
    private let settle = (k: CGFloat(380), zeta: CGFloat(0.68))
    private let trail = (k: CGFloat(900), zeta: CGFloat(0.82))
    private let catching = (k: CGFloat(700), zeta: CGFloat(0.7))
    private let ripple: TimeInterval = 0.03

    private(set) var bubbles: [Bubble] = []   // [0] is the top of the stack
    private let dismiss = DismissTarget()
    private var physics: Timer?
    private var watchdog: Timer?
    private var lastStep: TimeInterval = 0

    private var anchor = CGPoint.zero        // centre of the top bubble at rest
    private var onRight = true
    private var open = false
    private var shown: Bubble?
    private var quietUntil: TimeInterval = 0

    private var leader: Bubble?
    private var dragWhole = false
    private var caught = false
    private var reopenAfterDrag: Bubble?
    private var chain: [Bubble] = []
    private var samples: [(TimeInterval, CGPoint)] = []

    var onChange: (() -> Void)?
    var notice: ((String) -> Void)?

    var alwaysOnTop: Bool = UserDefaults.standard.object(forKey: "alwaysOnTop") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(alwaysOnTop, forKey: "alwaysOnTop")
            applyLevel()
        }
    }

    private var now: TimeInterval { ProcessInfo.processInfo.systemUptime }

    init() {
        let w = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.watch() }
        RunLoop.main.add(w, forMode: .common)
        watchdog = w

        // Clicking another app folds the open bubble away.
        NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main) { [weak self] note in
            guard let self = self,
                  let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
            self.appActivated(app)
        }
    }

    // MARK: add / remove

    func bubbleFrontWindow() {
        guard let window = WindowRef.focusedWindow() else {
            notice?(T("Click the window you want to bubble first.", "Klik eerst op het venster dat je wilt bubbelen."))
            return
        }
        add(window)
    }

    func add(_ window: WindowRef) {
        if let existing = bubbles.first(where: { $0.window.same(as: window) }) {
            if !open { openStack(existing) }
            return
        }
        let bubble = Bubble(window: window)
        bubble.view.delegate = self
        bubble.view.onNews = { [weak self, weak bubble] in self?.newsChanged(bubble) }
        let windowFrame = Coordinates.fromAX(window.frame)
        if window.isResizable {
            bubble.panelSize = CGSize(width: max(420, min(windowFrame.width, 900)), height: max(480, min(windowFrame.height, 900)))
        }

        hide(bubble)

        if bubbles.isEmpty {
            let screen = NSScreen.screens.first(where: { $0.frame.intersects(windowFrame) }) ?? NSScreen.main ?? NSScreen.screens[0]
            onRight = true
            anchor = CGPoint(x: edgeX(screen.visibleFrame, right: true, bubble: bubble),
                             y: screen.visibleFrame.maxY - screen.visibleFrame.height / 5)
        }

        bubbles.insert(bubble, at: 0)
        // Born where the window was, it flies into the stack.
        bubble.x = windowFrame.midX; bubble.y = windowFrame.midY
        bubble.tx = bubble.x; bubble.ty = bubble.y; bubble.nextTX = bubble.x; bubble.nextTY = bubble.y
        bubble.place()
        applyLevel()
        bubble.panel.orderFrontRegardless()
        bubble.view.popIn()

        layout(ripple: false)
        if open, let s = shown { show(s) }
        applyZOrder()
        onChange?()
    }

    /// Give the window back as it was before it became a bubble.
    func release(_ bubble: Bubble, activate: Bool) {
        if shown === bubble { shown = nil }
        if bubble.window.exists {
            reveal(bubble)
            bubble.window.setFrame(bubble.originalFrame)
            if activate {
                bubble.window.app?.activate(options: [.activateIgnoringOtherApps])
                bubble.window.raise()
            }
        }
        remove(bubble)
    }

    func releaseAll() {
        for b in bubbles { release(b, activate: false) }
    }

    private func remove(_ bubble: Bubble) {
        if shown === bubble { shown = nil }
        if leader === bubble { leader = nil; dragWhole = false; dismiss.hide() }
        if reopenAfterDrag === bubble { reopenAfterDrag = nil }
        chain.removeAll { $0 === bubble }
        bubbles.removeAll { $0 === bubble }
        bubble.panel.orderOut(nil)
        if bubbles.isEmpty || (open && shown == nil && leader == nil) { open = false }
        layout(ripple: true)
        applyZOrder()
        updateDot()
        onChange?()
    }

    // MARK: hiding and showing the real window

    /// An app with one window is hidden as a whole (no Dock clutter); otherwise the window
    /// is minimised.
    private func hide(_ bubble: Bubble) {
        quietUntil = now + 0.6
        if let app = bubble.window.app, WindowRef.windows(of: app).count <= 1 {
            bubble.hidApp = true
            app.hide()
        } else {
            bubble.hidApp = false
            bubble.window.setMinimized(true)
        }
    }

    private func reveal(_ bubble: Bubble) {
        quietUntil = now + 0.6
        if bubble.hidApp { bubble.window.app?.unhide() }
        if bubble.window.isMinimized { bubble.window.setMinimized(false) }
    }

    private func isHidden(_ bubble: Bubble) -> Bool {
        (bubble.hidApp && bubble.window.app?.isHidden == true) || bubble.window.isMinimized
    }

    // MARK: layout

    private var area: CGRect {
        (NSScreen.screens.first(where: { $0.frame.contains(anchor) }) ?? NSScreen.main ?? NSScreen.screens[0]).visibleFrame
    }

    private func edgeX(_ area: CGRect, right: Bool, bubble: Bubble) -> CGFloat {
        let r = bubble.diameter / 2
        return right ? area.maxX - 8 - r : area.minX + 8 + r
    }

    /// Closed: piled on the anchor, the next two peeking out. Open: a column down the edge.
    private func layout(ripple: Bool) {
        guard let first = bubbles.first else { return }
        let area = self.area
        let r = first.diameter / 2
        anchor.x = edgeX(area, right: onRight, bubble: first)
        anchor.y = max(area.minY + 8 + r, min(anchor.y, area.maxY - 8 - r))

        let step = first.diameter + 12
        var startY = anchor.y
        if open {
            let needed = CGFloat(bubbles.count - 1) * step
            startY = min(area.maxY - 8 - r, max(startY, area.minY + 8 + r + needed))
        }

        let t = now
        for (i, b) in bubbles.enumerated() {
            var x = anchor.x, y = startY
            if open {
                y = startY - CGFloat(i) * step
            } else {
                let peek = CGFloat(min(i, 2))
                x += (onRight ? 1 : -1) * peek * 3
                y -= peek * 5
            }
            b.nextTX = x; b.nextTY = y
            b.targetAt = ripple ? t + Double(i) * self.ripple : t
            if !ripple { b.tx = x; b.ty = y }
        }
        updateDot()
        startPhysics()
    }

    private func applyZOrder() {
        var order = bubbles
        if let l = leader { order.removeAll { $0 === l }; order.insert(l, at: 0) }
        for b in order.reversed() { b.panel.orderFrontRegardless() }
    }

    private func applyLevel() {
        let level: NSWindow.Level = alwaysOnTop ? .floating : .normal
        for b in bubbles { b.panel.level = level }
        applyZOrder()
    }

    private func newsChanged(_ bubble: Bubble?) {
        if let b = bubble, !open, bubbles.count > 1, b !== bubbles[0], b.hasNews { bubbles[0].view.startPulse() }
        updateDot()
    }

    private func updateDot() {
        for (i, b) in bubbles.enumerated() {
            b.view.showDot = i == 0 && !open && bubbles.dropFirst().contains { $0.hasNews }
        }
    }

    // MARK: physics

    private func startPhysics() {
        guard physics == nil else { return }
        lastStep = now
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.step() }
        RunLoop.main.add(timer, forMode: .common)
        physics = timer
    }

    private func step() {
        let t = now
        let dt = CGFloat(min(1.0 / 20, t - lastStep))
        lastStep = t
        var moving = false

        for b in bubbles {
            if t >= b.targetAt { b.tx = b.nextTX; b.ty = b.nextTY }
            if b === leader && !caught { b.place(); continue }

            var tx = b.tx, ty = b.ty, spring = settle
            if b === leader && caught {
                tx = dismiss.center.x; ty = dismiss.center.y; spring = catching
            } else if dragWhole, let index = chain.firstIndex(where: { $0 === b }), index > 0 {
                // Each bubble chases the one ahead of it: the trailing chain.
                let ahead = chain[index - 1]
                tx = ahead.x + (onRight ? 1 : -1) * 3
                ty = ahead.y - 5
                spring = trail
            }
            if integrate(b, tx: tx, ty: ty, k: spring.k, zeta: spring.zeta, dt: dt) { moving = true }
            b.place()
        }
        if !moving && leader == nil { physics?.invalidate(); physics = nil }
    }

    private func integrate(_ b: Bubble, tx: CGFloat, ty: CGFloat, k: CGFloat, zeta: CGFloat, dt: CGFloat) -> Bool {
        let c = 2 * zeta * sqrt(k)
        let h: CGFloat = 0.004
        var t: CGFloat = 0
        while t < dt {
            let ax = k * (tx - b.x) - c * b.vx
            let ay = k * (ty - b.y) - c * b.vy
            b.vx += ax * h; b.vy += ay * h
            b.x += b.vx * h; b.y += b.vy * h
            t += h
        }
        if abs(tx - b.x) < 0.4, abs(ty - b.y) < 0.4, abs(b.vx) < 8, abs(b.vy) < 8 {
            b.x = tx; b.y = ty; b.vx = 0; b.vy = 0
            return b.tx != b.nextTX || b.ty != b.nextTY
        }
        return true
    }

    // MARK: open / show / close

    func bubbleClicked(_ bubble: Bubble) {
        if !open { openStack(bubbles[0]) }
        else if bubble === shown { closeStack() }
        else { show(bubble) }
    }

    private func openStack(_ select: Bubble) {
        open = true
        layout(ripple: true)
        applyZOrder()
        show(select)
    }

    func closeStack() {
        if let last = shown {
            hide(last)
            last.expanded = false
            shown = nil
            bubbles.removeAll { $0 === last }
            bubbles.insert(last, at: 0)
        }
        open = false
        layout(ripple: true)
        applyZOrder()
    }

    private func show(_ bubble: Bubble) {
        guard bubble.window.exists else { remove(bubble); return }
        let previous = shown
        let r = bubble.diameter / 2
        let slot = CGRect(x: bubble.nextTX - r, y: bubble.nextTY - r, width: bubble.diameter, height: bubble.diameter)
        let area = self.area
        let gap: CGFloat = 12

        var size = bubble.panelSize
        if !bubble.window.isResizable { size = bubble.originalFrame.size }
        size.width = min(size.width, area.width - slot.width - 3 * gap)
        size.height = min(size.height, area.height - 2 * gap)
        let x = onRight ? slot.minX - gap - size.width : slot.maxX + gap
        let top = min(area.maxY - gap, max(slot.maxY, area.minY + gap + size.height))
        let frame = CGRect(x: x, y: top - size.height, width: size.width, height: size.height)

        shown = bubble
        bubble.expanded = true
        bubble.view.attention = false
        reveal(bubble)
        bubble.window.setFrame(Coordinates.toAX(frame))
        bubble.window.app?.activate(options: [.activateIgnoringOtherApps])
        bubble.window.raise()

        if let p = previous, p !== bubble {
            // Remember a size you gave it, then put it away.
            let f = p.window.frame
            if f.width > 100, f.height > 100 { p.panelSize = f.size }
            hide(p)
            p.expanded = false
        }
        applyZOrder()
    }

    private func appActivated(_ app: NSRunningApplication) {
        if alwaysOnTop { applyZOrder() }
        guard let s = shown, leader == nil, now > quietUntil else { return }
        if app.processIdentifier == s.window.pid || app.processIdentifier == ProcessInfo.processInfo.processIdentifier { return }
        let f = s.window.frame
        if f.width > 100, f.height > 100 { s.panelSize = f.size }
        closeStack()
    }

    func bubbleRelease(_ bubble: Bubble) { release(bubble, activate: true) }

    func bubbleCloseWindow(_ bubble: Bubble) {
        if !open { openStack(bubble) } else { show(bubble) }
        var button: CFTypeRef?
        if AXUIElementCopyAttributeValue(bubble.window.element, kAXCloseButtonAttribute as CFString, &button) == .success,
           let b = button {
            AXUIElementPerformAction(b as! AXUIElement, kAXPressAction as CFString)
        }
    }

    // MARK: dragging

    func bubbleDragStarted(_ bubble: Bubble) {
        leader = bubble
        caught = false
        samples.removeAll()
        bubble.vx = 0; bubble.vy = 0
        if !open {
            dragWhole = true
            chain = [bubble] + bubbles.filter { $0 !== bubble }
        } else {
            dragWhole = false
            reopenAfterDrag = shown
            if let s = shown { hide(s); s.expanded = false; shown = nil }
        }
        let screen = NSScreen.screens.first(where: { $0.frame.contains(CGPoint(x: bubble.x, y: bubble.y)) }) ?? NSScreen.screens[0]
        dismiss.show(on: screen)
        applyZOrder()
        startPhysics()
    }

    func bubbleDragged(_ bubble: Bubble, center: CGPoint) {
        guard bubble === leader else { return }
        let t = now
        samples.append((t, center))
        while samples.count > 2, let oldest = samples.first, t - oldest.0 > 0.1 { samples.removeFirst() }

        let d = hypot(center.x - dismiss.center.x, center.y - dismiss.center.y)
        let isCaught = d < dismiss.catchRadius
        if isCaught != caught { caught = isCaught; dismiss.setHot(isCaught) }
        if !isCaught {
            let v = velocity()
            bubble.x = center.x; bubble.y = center.y
            bubble.vx = v.x; bubble.vy = v.y
        }
        startPhysics()
    }

    private func velocity() -> CGPoint {
        guard let first = samples.first, let last = samples.last, last.0 - first.0 > 0.005 else { return .zero }
        let span = CGFloat(last.0 - first.0)
        return CGPoint(x: (last.1.x - first.1.x) / span, y: (last.1.y - first.1.y) / span)
    }

    func bubbleDragEnded(_ bubble: Bubble) {
        guard bubble === leader else { return }
        let release = caught, whole = dragWhole, v = velocity()
        dismiss.hide()
        leader = nil; dragWhole = false; caught = false

        if release {
            if whole { releaseAll() } else { self.release(bubble, activate: true) }
            return
        }
        if whole {
            // A flick carries on: aim where the stack would coast to, then pick that edge.
            let screen = NSScreen.screens.first(where: { $0.frame.contains(CGPoint(x: bubble.x, y: bubble.y)) }) ?? NSScreen.screens[0]
            let area = screen.visibleFrame
            let px = bubble.x + v.x * 0.2, py = bubble.y + v.y * 0.2
            onRight = px > area.midX
            anchor = CGPoint(x: edgeX(area, right: onRight, bubble: bubble), y: py)
            bubble.vx = v.x; bubble.vy = v.y
            layout(ripple: false)
        } else {
            layout(ripple: false)
            let reopen = reopenAfterDrag
            reopenAfterDrag = nil
            if let r = reopen, bubbles.contains(where: { $0 === r }) { show(r) } else { closeStack() }
        }
        applyZOrder()
    }

    // MARK: watching

    private func watch() {
        if leader != nil || now < quietUntil { return }
        for b in bubbles {
            if !b.window.exists { remove(b); continue }
            b.view.title = b.window.title
            if b !== shown && !isHidden(b) {
                // Brought back from the Dock or the app's own menu: respect that.
                remove(b)
            }
        }
    }
}
