import AppKit
import Carbon.HIToolbox

/// A key combination stored in Carbon terms (what RegisterEventHotKey wants).
struct Shortcut: Codable, Equatable {
    var keyCode: UInt32
    var modifiers: UInt32 // Carbon flags: cmdKey, optionKey, shiftKey, controlKey

    init(keyCode: UInt32, modifiers: UInt32) {
        self.keyCode = keyCode
        self.modifiers = modifiers
    }

    init(event: NSEvent) {
        keyCode = UInt32(event.keyCode)
        let f = event.modifierFlags
        var m: UInt32 = 0
        if f.contains(.command) { m |= UInt32(cmdKey) }
        if f.contains(.option) { m |= UInt32(optionKey) }
        if f.contains(.control) { m |= UInt32(controlKey) }
        if f.contains(.shift) { m |= UInt32(shiftKey) }
        modifiers = m
    }

    static let defaultCapture = Shortcut(keyCode: UInt32(kVK_ANSI_5), modifiers: UInt32(cmdKey | optionKey))

    var isFunctionKey: Bool { Self.functionKeys[Int(keyCode)] != nil }

    /// A global shortcut needs ⌘, ⌥ or ⌃ (or must be an F-key), or it would hijack normal typing.
    var isValid: Bool {
        isFunctionKey || modifiers & UInt32(cmdKey | optionKey | controlKey) != 0
    }

    var displayString: String {
        var s = ""
        if modifiers & UInt32(controlKey) != 0 { s += "⌃" }
        if modifiers & UInt32(optionKey) != 0 { s += "⌥" }
        if modifiers & UInt32(shiftKey) != 0 { s += "⇧" }
        if modifiers & UInt32(cmdKey) != 0 { s += "⌘" }
        return s + Self.keyName(Int(keyCode))
    }

    /// For showing the shortcut in a menu the native way (right-aligned, compact).
    var menuKeyEquivalent: (key: String, modifiers: NSEvent.ModifierFlags)? {
        var flags: NSEvent.ModifierFlags = []
        if modifiers & UInt32(cmdKey) != 0 { flags.insert(.command) }
        if modifiers & UInt32(optionKey) != 0 { flags.insert(.option) }
        if modifiers & UInt32(controlKey) != 0 { flags.insert(.control) }
        if modifiers & UInt32(shiftKey) != 0 { flags.insert(.shift) }

        let code = Int(keyCode)
        let fKeys = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
                     kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20]
        if let i = fKeys.firstIndex(of: code), let scalar = UnicodeScalar(NSF1FunctionKey + i) {
            return (String(Character(scalar)), flags)
        }
        let specials: [Int: Int] = [
            kVK_Space: 0x20, kVK_Return: 0x0D, kVK_Tab: 0x09, kVK_Delete: 0x08, kVK_Escape: 0x1B,
            kVK_ForwardDelete: NSDeleteFunctionKey, kVK_LeftArrow: NSLeftArrowFunctionKey,
            kVK_RightArrow: NSRightArrowFunctionKey, kVK_UpArrow: NSUpArrowFunctionKey,
            kVK_DownArrow: NSDownArrowFunctionKey, kVK_Home: NSHomeFunctionKey, kVK_End: NSEndFunctionKey,
            kVK_PageUp: NSPageUpFunctionKey, kVK_PageDown: NSPageDownFunctionKey,
        ]
        if let u = specials[code], let scalar = UnicodeScalar(u) { return (String(Character(scalar)), flags) }
        let name = Self.keyName(code)
        return name.count == 1 ? (name.lowercased(), flags) : nil
    }

    private static let functionKeys: [Int: String] = [
        kVK_F1: "F1", kVK_F2: "F2", kVK_F3: "F3", kVK_F4: "F4", kVK_F5: "F5", kVK_F6: "F6",
        kVK_F7: "F7", kVK_F8: "F8", kVK_F9: "F9", kVK_F10: "F10", kVK_F11: "F11", kVK_F12: "F12",
        kVK_F13: "F13", kVK_F14: "F14", kVK_F15: "F15", kVK_F16: "F16", kVK_F17: "F17", kVK_F18: "F18",
        kVK_F19: "F19", kVK_F20: "F20",
    ]

    private static let specialKeys: [Int: String] = [
        kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_Delete: "⌫", kVK_ForwardDelete: "⌦",
        kVK_Escape: "⎋", kVK_LeftArrow: "←", kVK_RightArrow: "→", kVK_UpArrow: "↑", kVK_DownArrow: "↓",
        kVK_Home: "↖", kVK_End: "↘", kVK_PageUp: "⇞", kVK_PageDown: "⇟",
    ]

    /// The character printed on the key in the user's current keyboard layout.
    private static func keyName(_ code: Int) -> String {
        if let f = functionKeys[code] { return f }
        if let s = specialKeys[code] { return s }
        guard let source = TISCopyCurrentKeyboardLayoutInputSource()?.takeRetainedValue(),
              let ptr = TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) else { return "?" }
        let data = Unmanaged<CFData>.fromOpaque(ptr).takeUnretainedValue() as Data
        var deadKeys: UInt32 = 0
        var chars = [UniChar](repeating: 0, count: 4)
        var length = 0
        let status = data.withUnsafeBytes { raw -> OSStatus in
            let layout = raw.bindMemory(to: UCKeyboardLayout.self).baseAddress!
            return UCKeyTranslate(layout, UInt16(code), UInt16(kUCKeyActionDisplay), 0, UInt32(LMGetKbdType()),
                                  OptionBits(kUCKeyTranslateNoDeadKeysBit), &deadKeys, chars.count, &length, &chars)
        }
        guard status == noErr, length > 0 else { return "?" }
        return String(utf16CodeUnits: chars, count: length).uppercased()
    }
}

/// Owns the app's system-wide shortcuts (Carbon RegisterEventHotKey: no Accessibility
/// permission needed).
final class HotKeyCenter {
    static let shared = HotKeyCenter()

    private var refs: [UInt32: EventHotKeyRef] = [:]
    private var actions: [UInt32: () -> Void] = [:]
    private var installed = false
    private(set) var failed: Set<String> = []

    /// Registers `bindings` (name → shortcut + action), replacing whatever was registered.
    func register(_ bindings: [(name: String, shortcut: Shortcut?, action: () -> Void)]) {
        unregisterAll()
        installHandlerIfNeeded()
        failed = []
        for (i, b) in bindings.enumerated() {
            guard let sc = b.shortcut else { continue }
            let id = UInt32(i + 1)
            var ref: EventHotKeyRef?
            let status = RegisterEventHotKey(sc.keyCode, sc.modifiers,
                                             EventHotKeyID(signature: OSType(0x54534E47), id: id), // 'TSNG'
                                             GetApplicationEventTarget(), 0, &ref)
            if status == noErr, let ref {
                refs[id] = ref
                actions[id] = b.action
            } else {
                failed.insert(b.name)
            }
        }
    }

    /// While recording a new shortcut, our own shortcuts must not fire.
    func unregisterAll() {
        refs.values.forEach { UnregisterEventHotKey($0) }
        refs = [:]
        actions = [:]
    }

    private func installHandlerIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
            var hk = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
            if let action = HotKeyCenter.shared.actions[hk.id] {
                DispatchQueue.main.async { action() }
            }
            return noErr
        }, 1, &spec, nil, nil)
    }
}
