import AppKit
import ApplicationServices

/// Somebody else's window, reached through the Accessibility API.
/// Coordinates here are AX coordinates: origin top-left of the main screen, y down.
final class WindowRef {
    let element: AXUIElement
    let pid: pid_t

    init(element: AXUIElement, pid: pid_t) {
        self.element = element
        self.pid = pid
    }

    var app: NSRunningApplication? { NSRunningApplication(processIdentifier: pid) }

    // MARK: attributes

    private func copy(_ attribute: String) -> (AXError, CFTypeRef?) {
        var value: CFTypeRef?
        let error = AXUIElementCopyAttributeValue(element, attribute as CFString, &value)
        return (error, value)
    }

    /// False once the window has been closed.
    var exists: Bool {
        let (error, _) = copy(kAXRoleAttribute)
        return error == .success
    }

    var title: String {
        let (_, value) = copy(kAXTitleAttribute)
        return (value as? String) ?? ""
    }

    var isMinimized: Bool {
        let (_, value) = copy(kAXMinimizedAttribute)
        return (value as? Bool) ?? false
    }

    var frame: CGRect {
        var origin = CGPoint.zero
        var size = CGSize.zero
        let (_, p) = copy(kAXPositionAttribute)
        let (_, s) = copy(kAXSizeAttribute)
        if let p = p, CFGetTypeID(p) == AXValueGetTypeID() { AXValueGetValue(p as! AXValue, .cgPoint, &origin) }
        if let s = s, CFGetTypeID(s) == AXValueGetTypeID() { AXValueGetValue(s as! AXValue, .cgSize, &size) }
        return CGRect(origin: origin, size: size)
    }

    var isResizable: Bool {
        var settable: DarwinBoolean = false
        AXUIElementIsAttributeSettable(element, kAXSizeAttribute as CFString, &settable)
        return settable.boolValue
    }

    func setFrame(_ rect: CGRect) {
        var origin = rect.origin
        var size = rect.size
        if let p = AXValueCreate(.cgPoint, &origin) {
            AXUIElementSetAttributeValue(element, kAXPositionAttribute as CFString, p)
        }
        if isResizable, let s = AXValueCreate(.cgSize, &size) {
            AXUIElementSetAttributeValue(element, kAXSizeAttribute as CFString, s)
        }
        // Setting the size can push the window; set the position once more.
        if let p = AXValueCreate(.cgPoint, &origin) {
            AXUIElementSetAttributeValue(element, kAXPositionAttribute as CFString, p)
        }
    }

    func setMinimized(_ minimized: Bool) {
        AXUIElementSetAttributeValue(element, kAXMinimizedAttribute as CFString, minimized as CFBoolean)
    }

    func raise() {
        AXUIElementPerformAction(element, kAXRaiseAction as CFString)
        AXUIElementSetAttributeValue(element, kAXMainAttribute as CFString, kCFBooleanTrue)
    }

    func same(as other: WindowRef) -> Bool {
        pid == other.pid && CFEqual(element, other.element)
    }

    // MARK: finding windows

    static func windows(of app: NSRunningApplication) -> [WindowRef] {
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(axApp, kAXWindowsAttribute as CFString, &value) == .success,
              let list = value as? [AXUIElement] else { return [] }
        return list.compactMap { element in
            let ref = WindowRef(element: element, pid: app.processIdentifier)
            var role: CFTypeRef?
            AXUIElementCopyAttributeValue(element, kAXSubroleAttribute as CFString, &role)
            // Only real document/app windows, not sheets, palettes or popovers.
            guard (role as? String) == (kAXStandardWindowSubrole as String) else { return nil }
            return ref
        }
    }

    static func focusedWindow() -> WindowRef? {
        guard let app = NSWorkspace.shared.frontmostApplication,
              app.processIdentifier != ProcessInfo.processInfo.processIdentifier else { return nil }
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(axApp, kAXFocusedWindowAttribute as CFString, &value) == .success,
              let window = value else { return nil }
        return WindowRef(element: window as! AXUIElement, pid: app.processIdentifier)
    }

    /// Every standard window of every ordinary app, for the menu.
    static func allWindows() -> [WindowRef] {
        let me = ProcessInfo.processInfo.processIdentifier
        return NSWorkspace.shared.runningApplications
            .filter { $0.activationPolicy == .regular && $0.processIdentifier != me }
            .flatMap { windows(of: $0) }
            .filter { !$0.title.isEmpty }
    }
}

/// AppKit uses a bottom-left origin with y up; the Accessibility API top-left with y down.
enum Coordinates {
    static var mainHeight: CGFloat { NSScreen.screens.first?.frame.maxY ?? 0 }

    static func toAX(_ rect: CGRect) -> CGRect {
        CGRect(x: rect.minX, y: mainHeight - rect.maxY, width: rect.width, height: rect.height)
    }

    static func fromAX(_ rect: CGRect) -> CGRect {
        CGRect(x: rect.minX, y: mainHeight - rect.maxY, width: rect.width, height: rect.height)
    }
}
