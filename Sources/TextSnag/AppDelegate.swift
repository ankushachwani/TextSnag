import AppKit
import AVFoundation
import Combine
import os

private let log = Logger(subsystem: "dev.textsnag.TextSnag", category: "capture")

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem!
    private var busy = false
    private let speech = AVSpeechSynthesizer()
    private let settings = Settings.shared
    private var subscriptions = Set<AnyCancellable>()

    // Selection tracking: for Direct Text, "Capture Previous Area", and timing.
    private var mouseMonitor: Any?
    private var dragStart: NSPoint?
    private var captureArea: CGRect? // this capture's area, if known
    private var lastArea: CGRect?
    private var mouseUpTime: Date?

    // MARK: Lifecycle

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = Self.menuBarIcon()
        statusItem.button?.setAccessibilityLabel("TextSnag")
        let menu = NSMenu()
        menu.delegate = self
        statusItem.menu = menu

        registerHotKeys()
        settings.shortcutsChanged
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.registerHotKeys() }
            .store(in: &subscriptions)

        DispatchQueue.global(qos: .userInitiated).async {
            let t = Date()
            OCR.warmUp()
            log.notice("warm-up took \(Date().timeIntervalSince(t), format: .fixed(precision: 3))s")
        }

        NSApp.mainMenu = makeMainMenu()

        if !CGPreflightScreenCaptureAccess() {
            CGRequestScreenCaptureAccess()
        }

        if !UserDefaults.standard.bool(forKey: "hasLaunchedBefore") {
            UserDefaults.standard.set(true, forKey: "hasLaunchedBefore")
            let combo = settings.shortcut(for: .capture)?.displayString ?? "the menu bar icon"
            Toast.show("TextSnag is in your menu bar. Press \(combo) to capture text.", symbol: "text.viewfinder")
        }
    }

    /// Opening the app again (e.g. double-clicking it in Applications) shows Settings.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        SettingsWindow.shared.show()
        return false
    }

    /// The "Lift" logo as a menu bar template image: a page with one line lifted out.
    static func menuBarIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { _ in
            NSColor.black.withAlphaComponent(0.5).setFill()
            NSBezierPath(roundedRect: NSRect(x: 3, y: 2.6, width: 10, height: 1.6), xRadius: 0.8, yRadius: 0.8).fill()
            NSBezierPath(roundedRect: NSRect(x: 3, y: 13.4, width: 8, height: 1.6), xRadius: 0.8, yRadius: 0.8).fill()
            NSColor.black.setFill()
            NSBezierPath(roundedRect: NSRect(x: 1.8, y: 6.6, width: 14.4, height: 4.2), xRadius: 1.2, yRadius: 1.2).fill()
            return true
        }
        image.isTemplate = true // macOS tints it for light/dark menu bars
        return image
    }

    /// Menu-bar apps have no visible main menu, but it's what makes ⌘W / ⌘Q / ⌘C work in the
    /// Settings window.
    private func makeMainMenu() -> NSMenu {
        let main = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: "Settings…", action: #selector(openSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: "Quit TextSnag", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        main.addItem(appItem)

        let editItem = NSMenuItem()
        let editMenu = NSMenu(title: "Edit")
        editMenu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        editMenu.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        editMenu.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = editMenu
        main.addItem(editItem)

        let windowItem = NSMenuItem()
        let windowMenu = NSMenu(title: "Window")
        windowMenu.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        windowItem.submenu = windowMenu
        main.addItem(windowItem)
        return main
    }

    private func registerHotKeys() {
        HotKeyCenter.shared.register(ShortcutAction.allCases.map { action in
            (action.rawValue, settings.shortcut(for: action), { [weak self] in self?.perform(action) })
        })
    }

    private func perform(_ action: ShortcutAction) {
        switch action {
        case .capture: startCapture(previous: false, keepLineBreaks: settings.keepLineBreaks)
        case .captureNoLineBreaks: startCapture(previous: false, keepLineBreaks: false)
        case .capturePrevious: startCapture(previous: true, keepLineBreaks: settings.keepLineBreaks)
        case .toggleAdditive:
            settings.additive.toggle()
            toast(settings.additive ? "Additive clipboard on" : "Additive clipboard off", "plus.square.on.square")
        }
    }

    // MARK: Capture

    @objc private func menuCapture() { perform(.capture) }
    @objc private func menuCapturePrevious() { perform(.capturePrevious) }

    private func startCapture(previous: Bool, keepLineBreaks: Bool) {
        guard !busy else { return }
        guard CGPreflightScreenCaptureAccess() else {
            requestScreenRecordingPermission()
            return
        }
        if !OCR.isWarm { toast("Getting ready (first launch only)…", "hourglass") }
        busy = true
        captureArea = nil

        let handle: (CGImage?) -> Void = { [weak self] image in
            self?.stopTrackingMouse()
            self?.recognize(image, keepLineBreaks: keepLineBreaks)
        }
        if previous, let area = lastArea ?? ScreenCapture.lastSelection() {
            mouseUpTime = Date()
            captureArea = area
            ScreenCapture.rect(area, completion: handle)
        } else {
            startTrackingMouse()
            ScreenCapture.interactive(completion: handle)
        }
    }

    private func recognize(_ image: CGImage?, keepLineBreaks: Bool) {
        guard let image else { busy = false; return } // user pressed Esc
        let grabbed = Date()
        let options = settings.formatOptions(keepLineBreaks: keepLineBreaks)
        let area = (settings.directText && DirectText.isTrusted) ? captureArea : nil

        DispatchQueue.global(qos: .userInitiated).async {
            // Direct Text reads the app's own text while OCR reads the pixels, in parallel.
            var direct: String?
            let directDone = DispatchGroup()
            if let area {
                directDone.enter()
                DispatchQueue.global(qos: .userInitiated).async {
                    direct = DirectText.text(in: area)
                    directDone.leave()
                }
            }
            var result = OCR.recognize(image, options: options)
            directDone.wait()

            // Use the app's text only when it matches what's visible (tables keep their columns).
            if let direct, !result.raw.isEmpty, DirectText.agrees(direct: direct, ocr: result.raw) {
                if case .table = result.kind {} else {
                    result.text = TextFormatter.formatDirect(direct, kind: result.kind, options: options)
                    result.isDirectText = true
                }
            }
            let elapsed = Date().timeIntervalSince(grabbed)
            DispatchQueue.main.async { self.deliver(result, elapsed: elapsed) }
        }
    }

    private func deliver(_ result: OCRResult, elapsed: TimeInterval) {
        busy = false
        guard !result.isEmpty else {
            toast("No text found", "text.viewfinder")
            return
        }

        var output = result.combined
        if settings.additive {
            output = settings.additiveBuffer.isEmpty ? output : settings.additiveBuffer + "\n" + output
            settings.additiveBuffer = output
        }
        copyToClipboard(output)

        if let up = mouseUpTime {
            log.notice("""
                selection → clipboard \(Date().timeIntervalSince(up), format: .fixed(precision: 3))s \
                (recognition \(elapsed, format: .fixed(precision: 3))s, \(result.kind.label, privacy: .public)\
                \(result.isDirectText ? ", direct text" : "", privacy: .public))
                """)
        }
        mouseUpTime = nil

        if settings.playSound { NSSound(named: "Tink")?.play() }
        if settings.showToast {
            let actions = (settings.quickActions && !settings.additive) ? quickActions(for: result) : []
            Toast.show(message(for: result), symbol: symbol(for: result), actions: actions)
        }

        if settings.openLinks {
            for code in result.codes {
                if let url = URL(string: code), let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme) {
                    NSWorkspace.shared.open(url)
                }
            }
        }
        if settings.speak, !result.text.isEmpty {
            speech.stopSpeaking(at: .immediate)
            speech.speak(AVSpeechUtterance(string: result.text))
        }
    }

    private func message(for result: OCRResult) -> String {
        if settings.additive { return "Added text" }
        if !result.codes.isEmpty { return result.text.isEmpty ? "Copied QR code" : "Copied text + QR code" }
        if result.isDirectText { return "Copied exact text" }
        switch result.kind {
        case .code: return "Copied code"
        case .table: return "Copied table"
        case .prose: return "Copied text"
        }
    }

    private func symbol(for result: OCRResult) -> String {
        if !result.codes.isEmpty { return "qrcode" }
        if result.isDirectText { return "checkmark.seal" }
        switch result.kind {
        case .code: return "chevron.left.forwardslash.chevron.right"
        case .table: return "tablecells"
        case .prose: return "doc.on.clipboard"
        }
    }

    // MARK: Quick actions

    /// One-click follow-ups offered in the bubble, chosen from what was captured.
    private func quickActions(for result: OCRResult) -> [ToastAction] {
        let text = result.text
        var actions: [ToastAction] = []
        let copy: (String, String) -> String? = { [weak self] value, confirmation in
            self?.copyToClipboard(value)
            return confirmation
        }

        if case .table(let rows) = result.kind {
            actions.append(ToastAction(title: "Markdown") { copy(TextFormatter.markdownTable(rows), "Copied as Markdown") })
        }

        // Links, emails and phone numbers found in the text.
        if let detector = try? NSDataDetector(types: NSTextCheckingResult.CheckingType.link.rawValue
                                              | NSTextCheckingResult.CheckingType.phoneNumber.rawValue) {
            let matches = detector.matches(in: text, range: NSRange(text.startIndex..., in: text))
            if let link = matches.first(where: { $0.resultType == .link })?.url {
                let isMail = link.scheme == "mailto"
                actions.append(ToastAction(title: isMail ? "Email" : "Open Link") {
                    NSWorkspace.shared.open(link)
                    return nil
                })
            }
            if let phone = matches.first(where: { $0.resultType == .phoneNumber })?.phoneNumber {
                let digits = phone.filter { $0.isNumber || $0 == "+" }
                if let url = URL(string: "tel:\(digits)") {
                    actions.append(ToastAction(title: "Call") {
                        NSWorkspace.shared.open(url)
                        return nil
                    })
                }
            }
        }

        if text.contains("\n") {
            actions.append(ToastAction(title: "One Line") { copy(TextFormatter.oneLine(text), "Copied as one line") })
        }
        let numbers = TextFormatter.numbers(text)
        if !numbers.isEmpty, numbers.joined(separator: " ") != text.trimmingCharacters(in: .whitespacesAndNewlines) {
            actions.append(ToastAction(title: "Numbers") { copy(numbers.joined(separator: "\n"), "Copied numbers only") })
        }
        if !result.isDirectText, !result.raw.isEmpty, result.raw != text {
            actions.append(ToastAction(title: "Original") { copy(result.raw, "Copied without formatting") })
        }
        return Array(actions.prefix(4))
    }

    private func copyToClipboard(_ text: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(text, forType: .string)
    }

    private func toast(_ message: String, _ symbol: String) {
        if settings.showToast { Toast.show(message, symbol: symbol) }
    }

    /// Watches the drag the user makes in the crosshair so we know the exact area
    /// (for Direct Text and "Capture Previous Area") and when they let go (for timing).
    /// Global mouse monitors need no extra permission.
    private func startTrackingMouse() {
        dragStart = nil
        mouseMonitor = NSEvent.addGlobalMonitorForEvents(matching: [.leftMouseDown, .leftMouseUp]) { [weak self] e in
            guard let self else { return }
            let p = NSEvent.mouseLocation
            if e.type == .leftMouseDown {
                self.dragStart = p
            } else if let s = self.dragStart {
                self.mouseUpTime = Date()
                // Convert from Cocoa (bottom-left) to screen coordinates (top-left).
                let top = NSScreen.screens.first?.frame.maxY ?? 0
                let rect = CGRect(x: min(s.x, p.x), y: top - max(s.y, p.y), width: abs(p.x - s.x), height: abs(p.y - s.y))
                if rect.width > 4, rect.height > 4 {
                    self.lastArea = rect
                    self.captureArea = rect
                }
            }
        }
    }

    private func stopTrackingMouse() {
        if let mouseMonitor { NSEvent.removeMonitor(mouseMonitor) }
        mouseMonitor = nil
    }

    private func requestScreenRecordingPermission() {
        if !CGRequestScreenCaptureAccess() {
            let alert = NSAlert()
            alert.messageText = "TextSnag needs Screen Recording permission"
            alert.informativeText = "Turn on TextSnag in System Settings → Privacy & Security → Screen Recording, then quit and reopen TextSnag."
            alert.addButton(withTitle: "Open System Settings")
            alert.addButton(withTitle: "Cancel")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn {
                NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
            }
        }
    }

    // MARK: Menu

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()

        menu.addItem(item("Start Capture", #selector(menuCapture), shortcut: settings.shortcut(for: .capture)))
        let prev = item("Capture Previous Area", #selector(menuCapturePrevious),
                        shortcut: settings.shortcut(for: .capturePrevious))
        prev.isEnabled = (lastArea ?? ScreenCapture.lastSelection()) != nil
        menu.addItem(prev)
        let failed = HotKeyCenter.shared.failed
        if !failed.isEmpty {
            let names = ShortcutAction.allCases.filter { failed.contains($0.rawValue) }.map(\.title).joined(separator: ", ")
            let warn = NSMenuItem(title: "⚠︎ Shortcut unavailable: \(names)", action: nil, keyEquivalent: "")
            warn.isEnabled = false
            menu.addItem(warn)
        }

        menu.addItem(.separator())
        menu.addItem(toggle("Keep Line Breaks", settings.keepLineBreaks, #selector(toggleLineBreaks)))
        menu.addItem(toggle("Additive Clipboard", settings.additive, #selector(toggleAdditive)))
        if settings.additive {
            let clear = item("Clear Additive Clipboard", #selector(clearAdditive))
            clear.isEnabled = !settings.additiveBuffer.isEmpty
            menu.addItem(clear)
        }
        menu.addItem(toggle("Read Text Aloud", settings.speak, #selector(toggleSpeak)))
        if speech.isSpeaking { menu.addItem(item("Stop Speaking", #selector(stopSpeaking))) }

        menu.addItem(.separator())
        let settingsItem = item("Settings…", #selector(openSettings))
        settingsItem.keyEquivalent = ","
        menu.addItem(settingsItem)
        menu.addItem(NSMenuItem(title: "Quit TextSnag", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
    }

    private func item(_ title: String, _ action: Selector, shortcut: Shortcut? = nil) -> NSMenuItem {
        let mi = NSMenuItem(title: title, action: action, keyEquivalent: "")
        mi.target = self
        if let equivalent = shortcut?.menuKeyEquivalent {
            // Shown on the right like any menu shortcut. Pressing it while the menu is open
            // runs the same action as the global shortcut.
            mi.keyEquivalent = equivalent.key
            mi.keyEquivalentModifierMask = equivalent.modifiers
        }
        return mi
    }

    private func toggle(_ title: String, _ on: Bool, _ action: Selector) -> NSMenuItem {
        let mi = item(title, action)
        mi.state = on ? .on : .off
        return mi
    }

    @objc private func toggleLineBreaks() { settings.keepLineBreaks.toggle() }
    @objc private func toggleAdditive() { settings.additive.toggle() }
    @objc private func clearAdditive() {
        settings.additiveBuffer = ""
        toast("Additive clipboard cleared", "trash")
    }
    @objc private func toggleSpeak() { settings.speak.toggle() }
    @objc private func stopSpeaking() { speech.stopSpeaking(at: .immediate) }
    @objc private func openSettings() { SettingsWindow.shared.show() }
}
