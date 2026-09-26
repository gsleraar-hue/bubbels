import AppKit
import Carbon
import ServiceManagement

/// The global hotkey arrives through a C callback that cannot capture anything.
var hotKeyAction: (() -> Void)?

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private let manager = BubbleManager()
    private var hotKeyRef: EventHotKeyRef?

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = Self.menuBarIcon()
        statusItem.button?.toolTip = "Bubbels"
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        manager.notice = { text in
            let alert = NSAlert()
            alert.messageText = "Bubbels"
            alert.informativeText = text
            alert.runModal()
        }

        registerHotKey()

        // Moving and hiding other apps' windows needs Accessibility access.
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        if !AXIsProcessTrustedWithOptions(options) {
            NSLog("Bubbels: waiting for Accessibility access")
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        // Never leave windows hidden behind.
        manager.releaseAll()
    }

    // MARK: menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        if !AXIsProcessTrusted() {
            menu.addItem(item(T("Allow Accessibility access…", "Toegang tot toegankelijkheid geven…"), #selector(openAccessibility)))
            menu.addItem(.separator())
        }

        let pick = NSMenuItem(title: T("Put a window in a bubble", "Venster in een bubbel"), action: nil, keyEquivalent: "")
        let windows = NSMenu()
        for window in WindowRef.allWindows() {
            var title = window.title
            if title.count > 60 { title = String(title.prefix(57)) + "…" }
            let entry = NSMenuItem(title: title, action: #selector(bubbleWindow(_:)), keyEquivalent: "")
            entry.target = self
            entry.representedObject = window
            if let icon = window.app?.icon {
                icon.size = NSSize(width: 16, height: 16)
                entry.image = icon
            }
            windows.addItem(entry)
        }
        if windows.items.isEmpty {
            let none = NSMenuItem(title: T("No windows found", "Geen vensters gevonden"), action: nil, keyEquivalent: "")
            none.isEnabled = false
            windows.addItem(none)
        }
        pick.submenu = windows
        menu.addItem(pick)

        let front = item(T("Bubble the front window", "Voorste venster bubbelen"), #selector(bubbleFront))
        front.keyEquivalent = "o"
        front.keyEquivalentModifierMask = [.control, .option]
        menu.addItem(front)

        menu.addItem(.separator())
        let releaseAll = item(T("Release all bubbles", "Alle bubbels terugzetten"), #selector(releaseAll))
        releaseAll.isEnabled = !manager.bubbles.isEmpty
        menu.addItem(releaseAll)

        let onTop = item(T("Bubbles always on top", "Bubbels altijd bovenop"), #selector(toggleOnTop))
        onTop.state = manager.alwaysOnTop ? .on : .off
        menu.addItem(onTop)

        let login = item(T("Open at login", "Openen bij inloggen"), #selector(toggleLogin))
        login.state = SMAppService.mainApp.status == .enabled ? .on : .off
        menu.addItem(login)

        menu.addItem(.separator())
        menu.addItem(item(T("Quit Bubbels", "Stop Bubbels"), #selector(quit)))
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        return item
    }

    @objc private func bubbleWindow(_ sender: NSMenuItem) {
        if let window = sender.representedObject as? WindowRef { manager.add(window) }
    }

    @objc private func bubbleFront() { manager.bubbleFrontWindow() }
    @objc private func releaseAll() { manager.releaseAll() }
    @objc private func toggleOnTop() { manager.alwaysOnTop.toggle() }
    @objc private func quit() { NSApp.terminate(nil) }

    @objc private func openAccessibility() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            manager.notice?(T("Could not change the login item: ", "Kon het inlogonderdeel niet wijzigen: ") + error.localizedDescription)
        }
    }

    // MARK: hotkey (Control-Option-O)

    private func registerHotKey() {
        hotKeyAction = { [weak self] in self?.manager.bubbleFrontWindow() }
        var eventType = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ -> OSStatus in
            DispatchQueue.main.async { hotKeyAction?() }
            return noErr
        }, 1, &eventType, nil, nil)
        let id = EventHotKeyID(signature: OSType(0x4255_424C), id: 1)   // "BUBL"
        RegisterEventHotKey(UInt32(kVK_ANSI_O), UInt32(controlKey | optionKey), id,
                            GetApplicationEventTarget(), 0, &hotKeyRef)
    }

    // MARK: icon

    /// Two bubbles, drawn as a template image so the menu bar tints it.
    private static func menuBarIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { _ in
            NSColor.black.setFill()
            NSBezierPath(ovalIn: NSRect(x: 1, y: 1, width: 12, height: 12)).fill()
            let small = NSBezierPath(ovalIn: NSRect(x: 11, y: 10, width: 6.5, height: 6.5))
            small.lineWidth = 1.6
            NSColor.black.setStroke()
            small.stroke()
            return true
        }
        image.isTemplate = true
        return image
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
