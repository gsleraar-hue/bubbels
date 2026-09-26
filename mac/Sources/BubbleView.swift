import AppKit

/// Texts: English, or Dutch when the Mac is set to Dutch.
func T(_ english: String, _ dutch: String) -> String {
    (Locale.preferredLanguages.first ?? "en").hasPrefix("nl") ? dutch : english
}

/// A borderless, never-activating panel that floats above ordinary windows on every Space.
class FloatingPanel: NSPanel {
    init(size: CGFloat) {
        super.init(contentRect: NSRect(x: 0, y: 0, width: size, height: size),
                   styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        isMovable = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

/// One bubble: the window it stands for, its spring state, and how it looks.
final class Bubble {
    let window: WindowRef
    let panel: FloatingPanel
    let view: BubbleView
    let diameter: CGFloat = 56
    let inset: CGFloat = 18
    var panelSize: CGSize
    var originalFrame: CGRect          // AX coordinates
    var hidApp = false                 // hidden via the app (single window) instead of minimised
    var expanded = false { didSet { view.needsDisplay = true } }

    // Spring physics in AppKit screen coordinates: centre, velocity (pt/s), target.
    var x: CGFloat = 0, y: CGFloat = 0, vx: CGFloat = 0, vy: CGFloat = 0
    var tx: CGFloat = 0, ty: CGFloat = 0, nextTX: CGFloat = 0, nextTY: CGFloat = 0
    var targetAt: TimeInterval = 0

    var size: CGFloat { diameter + 2 * inset }

    init(window: WindowRef) {
        self.window = window
        originalFrame = window.frame
        panelSize = originalFrame.size
        panel = FloatingPanel(size: diameter + 2 * inset)
        view = BubbleView(frame: NSRect(x: 0, y: 0, width: diameter + 2 * inset, height: diameter + 2 * inset))
        view.bubble = self
        view.icon = window.app?.icon
        view.title = window.title
        panel.contentView = view
    }

    func place() {
        panel.setFrameOrigin(NSPoint(x: (x - size / 2).rounded(), y: (y - size / 2).rounded()))
    }

    var hasNews: Bool { view.badge > 0 || view.attention }
}

protocol BubbleViewDelegate: AnyObject {
    func bubbleClicked(_ bubble: Bubble)
    func bubbleDragStarted(_ bubble: Bubble)
    func bubbleDragged(_ bubble: Bubble, center: CGPoint)
    func bubbleDragEnded(_ bubble: Bubble)
    func bubbleRelease(_ bubble: Bubble)
    func bubbleCloseWindow(_ bubble: Bubble)
}

final class BubbleView: NSView {
    weak var bubble: Bubble?
    weak var delegate: BubbleViewDelegate?
    var icon: NSImage?
    var title: String = "" { didSet { titleChanged(oldValue) } }
    var badge = 0
    var attention = false { didSet { needsDisplay = true } }
    var showDot = false { didSet { if showDot != oldValue { needsDisplay = true } } }
    var onNews: (() -> Void)?

    private var scale: CGFloat = 1
    private var pulse: CGFloat = -1
    private var animation: Timer?
    private var popStart: TimeInterval = 0
    private var pulseStart: TimeInterval = 0
    private var popping = false
    private var pulses = 0

    private var downPoint: NSPoint = .zero
    private var grabOffset: CGPoint = .zero
    private var dragging = false

    override var isFlipped: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    // MARK: news

    private func titleChanged(_ old: String) {
        toolTip = title
        // WhatsApp, Slack, Mail and friends put the unread count in the title: "(3) WhatsApp".
        var count = 0
        if let match = title.range(of: #"^\s*[\(\[](\d{1,4})\+?[\)\]]"#, options: .regularExpression) {
            let digits = title[match].filter { $0.isNumber }
            count = Int(digits) ?? 0
        }
        if count != badge {
            let grew = count > badge
            badge = count
            if grew, bubble?.expanded == false { startPulse() }
            needsDisplay = true
            onNews?()
        }
    }

    // MARK: animation

    func popIn() {
        scale = 0
        popping = true
        popStart = ProcessInfo.processInfo.systemUptime
        runAnimation()
    }

    func startPulse() {
        pulses = 3
        pulse = 0
        pulseStart = ProcessInfo.processInfo.systemUptime
        runAnimation()
    }

    private func runAnimation() {
        guard animation == nil else { return }
        let timer = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(timer, forMode: .common)
        animation = timer
    }

    private func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        var busy = false
        if popping {
            let t = min(1, (now - popStart) / 0.26)
            // Ease-out-back: a little overshoot, like a bubble that just formed.
            let c = 1.9, x = t - 1
            scale = CGFloat(1 + (c + 1) * x * x * x + c * x * x)
            if t >= 1 { popping = false; scale = 1 } else { busy = true }
        }
        if pulses > 0 {
            let period = 0.85
            let elapsed = now - pulseStart - Double(3 - pulses) * period
            pulse = CGFloat(max(0, min(1, elapsed / period)))
            if elapsed >= period { pulses -= 1 }
            if pulses == 0 { pulse = -1 } else { busy = true }
        }
        needsDisplay = true
        if !busy { animation?.invalidate(); animation = nil }
    }

    // MARK: mouse

    override func mouseDown(with event: NSEvent) {
        downPoint = NSEvent.mouseLocation
        dragging = false
        if let b = bubble { grabOffset = CGPoint(x: downPoint.x - b.x, y: downPoint.y - b.y) }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let b = bubble else { return }
        let now = NSEvent.mouseLocation
        if !dragging, hypot(now.x - downPoint.x, now.y - downPoint.y) > 4 {
            dragging = true
            delegate?.bubbleDragStarted(b)
        }
        if dragging {
            delegate?.bubbleDragged(b, center: CGPoint(x: now.x - grabOffset.x, y: now.y - grabOffset.y))
        }
    }

    override func mouseUp(with event: NSEvent) {
        guard let b = bubble else { return }
        if dragging { dragging = false; delegate?.bubbleDragEnded(b) }
        else { delegate?.bubbleClicked(b) }
    }

    override func rightMouseDown(with event: NSEvent) {
        guard let b = bubble else { return }
        let menu = NSMenu()
        let toggle = NSMenuItem(title: b.expanded ? T("Fold away", "Inklappen") : T("Open", "Openen"),
                                action: #selector(menuToggle), keyEquivalent: "")
        toggle.target = self
        menu.addItem(toggle)
        let release = NSMenuItem(title: T("Turn back into a normal window", "Terugzetten als gewoon venster"),
                                 action: #selector(menuRelease), keyEquivalent: "")
        release.target = self
        menu.addItem(release)
        menu.addItem(.separator())
        let close = NSMenuItem(title: T("Close window", "Venster sluiten"), action: #selector(menuClose), keyEquivalent: "")
        close.target = self
        menu.addItem(close)
        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }

    @objc private func menuToggle() { if let b = bubble { delegate?.bubbleClicked(b) } }
    @objc private func menuRelease() { if let b = bubble { delegate?.bubbleRelease(b) } }
    @objc private func menuClose() { if let b = bubble { delegate?.bubbleCloseWindow(b) } }

    // MARK: drawing

    override func draw(_ dirtyRect: NSRect) {
        guard let b = bubble else { return }
        NSColor.clear.set()
        bounds.fill()

        let c = CGPoint(x: bounds.midX, y: bounds.midY)
        let r = b.diameter / 2 * scale
        guard r > 1 else { return }
        let dark = effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        let accent = NSColor.controlAccentColor

        if pulse >= 0 {
            let pr = r + (b.inset - 2) * pulse
            accent.withAlphaComponent(0.8 * (1 - pulse)).setStroke()
            let ring = NSBezierPath(ovalIn: NSRect(x: c.x - pr, y: c.y - pr, width: pr * 2, height: pr * 2))
            ring.lineWidth = 3
            ring.stroke()
        }

        let circle = NSRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2)
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(dark ? 0.55 : 0.3)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        shadow.set()
        (dark ? NSColor(white: 0.18, alpha: 1) : NSColor.white).setFill()
        NSBezierPath(ovalIn: circle).fill()
        NSGraphicsContext.restoreGraphicsState()

        (dark ? NSColor.white.withAlphaComponent(0.18) : NSColor.black.withAlphaComponent(0.12)).setStroke()
        NSBezierPath(ovalIn: circle.insetBy(dx: 0.5, dy: 0.5)).stroke()

        if let icon = icon {
            let s = b.diameter * 0.68 * scale
            icon.draw(in: NSRect(x: c.x - s / 2, y: c.y - s / 2, width: s, height: s))
        } else {
            let letter = String(title.trimmingCharacters(in: .whitespaces).prefix(1)).uppercased()
            let attrs: [NSAttributedString.Key: Any] = [
                .font: NSFont.systemFont(ofSize: r * 0.9, weight: .semibold),
                .foregroundColor: NSColor.labelColor]
            let size = letter.size(withAttributes: attrs)
            letter.draw(at: NSPoint(x: c.x - size.width / 2, y: c.y - size.height / 2), withAttributes: attrs)
        }

        if b.expanded {
            let er = r + 4
            accent.setStroke()
            let ring = NSBezierPath(ovalIn: NSRect(x: c.x - er, y: c.y - er, width: er * 2, height: er * 2))
            ring.lineWidth = 2.5
            ring.stroke()
        }

        if scale > 0.8, badge > 0 || attention || showDot {
            drawBadge(center: CGPoint(x: c.x + r * 0.72, y: c.y + r * 0.72), dark: dark, diameter: b.diameter)
        }
    }

    private func drawBadge(center: CGPoint, dark: Bool, diameter: CGFloat) {
        let text: String? = badge > 99 ? "99+" : badge > 0 ? String(badge) : nil
        let h = text == nil ? diameter * 0.26 : diameter * 0.36
        var w = h
        let attrs: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: h * 0.6, weight: .bold),
            .foregroundColor: NSColor.white]
        if let text = text { w = max(h, text.size(withAttributes: attrs).width + h * 0.5) }
        let rect = NSRect(x: center.x - w / 2, y: center.y - h / 2, width: w, height: h)
        let pill = NSBezierPath(roundedRect: rect, xRadius: h / 2, yRadius: h / 2)
        NSColor.systemRed.setFill()
        pill.fill()
        (dark ? NSColor(white: 0.18, alpha: 1) : NSColor.white).setStroke()
        pill.lineWidth = 2
        pill.stroke()
        if let text = text {
            let size = text.size(withAttributes: attrs)
            text.draw(at: NSPoint(x: rect.midX - size.width / 2, y: rect.midY - size.height / 2), withAttributes: attrs)
        }
    }
}

/// The round "drop here to let go" target that rises while you drag.
final class DismissTarget {
    let panel: FloatingPanel
    private let view: DismissView
    private var timer: Timer?
    private var shownAt: TimeInterval = 0
    private var rest: CGPoint = .zero
    private(set) var hot = false
    let catchRadius: CGFloat = 60

    init() {
        panel = FloatingPanel(size: 110)
        view = DismissView(frame: NSRect(x: 0, y: 0, width: 110, height: 110))
        panel.contentView = view
        panel.ignoresMouseEvents = true
        panel.level = .popUpMenu
    }

    var center: CGPoint { rest }

    func show(on screen: NSScreen) {
        let area = screen.visibleFrame
        rest = CGPoint(x: area.midX, y: area.minY + 70)
        view.appear = 0
        view.hot = 0
        view.hotVelocity = 0
        hot = false
        shownAt = ProcessInfo.processInfo.systemUptime
        panel.setFrameOrigin(NSPoint(x: rest.x - 55, y: rest.y - 55 - 60))
        panel.orderFrontRegardless()
        start()
    }

    func hide() {
        timer?.invalidate()
        timer = nil
        panel.orderOut(nil)
    }

    func setHot(_ value: Bool) {
        guard value != hot else { return }
        hot = value
        start()
    }

    private func start() {
        guard timer == nil else { return }
        let t = Timer(timeInterval: 1.0 / 60, repeats: true) { [weak self] _ in self?.tick() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func tick() {
        let elapsed = ProcessInfo.processInfo.systemUptime - shownAt
        view.appear = CGFloat(min(1, elapsed / 0.22))
        let eased = 1 - pow(1 - view.appear, 3)
        panel.setFrameOrigin(NSPoint(x: rest.x - 55, y: rest.y - 55 - 60 * (1 - eased)))

        // Underdamped spring: it wobbles a little when it catches a bubble.
        let target: CGFloat = hot ? 1 : 0
        var t: CGFloat = 0
        while t < 1.0 / 60 {
            let a = 500 * (target - view.hot) - 18 * view.hotVelocity
            view.hotVelocity += a * 0.004
            view.hot += view.hotVelocity * 0.004
            t += 0.004
        }
        view.needsDisplay = true
        if view.appear >= 1, abs(target - view.hot) < 0.002, abs(view.hotVelocity) < 0.01 {
            view.hot = target
            timer?.invalidate()
            timer = nil
        }
    }
}

final class DismissView: NSView {
    var appear: CGFloat = 0
    var hot: CGFloat = 0
    var hotVelocity: CGFloat = 0

    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.set()
        bounds.fill()
        let eased = 1 - pow(1 - appear, 3)
        let c = CGPoint(x: bounds.midX, y: bounds.midY)
        let r = 30 * (0.7 + 0.3 * eased) * (1 + 0.25 * hot)
        let h = max(0, min(1, hot))
        let fill = NSColor(calibratedRed: 0.13 + 0.7 * h, green: 0.13 + 0.05 * h, blue: 0.14 + 0.04 * h,
                           alpha: (0.8 + 0.12 * h) * eased)
        fill.setFill()
        let circle = NSBezierPath(ovalIn: NSRect(x: c.x - r, y: c.y - r, width: r * 2, height: r * 2))
        circle.fill()
        NSColor.white.withAlphaComponent(0.5 * eased).setStroke()
        circle.lineWidth = 1.5
        circle.stroke()

        let x = r * 0.32
        let cross = NSBezierPath()
        cross.move(to: NSPoint(x: c.x - x, y: c.y - x)); cross.line(to: NSPoint(x: c.x + x, y: c.y + x))
        cross.move(to: NSPoint(x: c.x - x, y: c.y + x)); cross.line(to: NSPoint(x: c.x + x, y: c.y - x))
        cross.lineWidth = 3.5
        cross.lineCapStyle = .round
        NSColor.white.withAlphaComponent(eased).setStroke()
        cross.stroke()
    }
}
