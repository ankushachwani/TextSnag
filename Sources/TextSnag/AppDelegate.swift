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

    // Selection tracking, for "Capture Previous Area" and timing.
    private var mouseMonitor: Any?
    private var dragStart: NSPoint?
    private var lastArea: CGRect?
    private var mouseUpTime: Date?

    // MARK: Lifecycle

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        statusItem.button?.image = NSImage(systemSymbolName: "text.viewfinder", accessibilityDescription: "TextSnag")
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

        let handle: (CGImage?) -> Void = { [weak self] image in
            self?.stopTrackingMouse()
            self?.recognize(image, keepLineBreaks: keepLineBreaks)
        }
        if previous, let area = lastArea ?? ScreenCapture.lastSelection() {
            mouseUpTime = Date()
            ScreenCapture.rect(area, completion: handle)
        } else {
            startTrackingMouse()
            ScreenCapture.interactive(completion: handle)
        }
    }

    private func recognize(_ image: CGImage?, keepLineBreaks: Bool) {
        guard let image else { busy = false; return } // user pressed Esc
        let grabbed = Date()
        DispatchQueue.global(qos: .userInitiated).async {
            let result = OCR.recognize(image, keepLineBreaks: keepLineBreaks)
            let ocrTime = Date().timeIntervalSince(grabbed)
            DispatchQueue.main.async { self.deliver(result, ocrTime: ocrTime) }
        }
    }

    private func deliver(_ result: OCRResult, ocrTime: TimeInterval) {
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
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(output, forType: .string)

        if let up = mouseUpTime {
            log.notice("""
                selection → clipboard \(Date().timeIntervalSince(up), format: .fixed(precision: 3))s \
                (ocr \(ocrTime, format: .fixed(precision: 3))s)
                """)
        }
        mouseUpTime = nil

        if settings.playSound { NSSound(named: "Tink")?.play() }
        if !result.codes.isEmpty {
            toast(result.text.isEmpty ? "Copied QR / barcode" : "Copied text + QR / barcode", "qrcode")
        } else {
            let n = result.text.count
            toast(settings.additive ? "Added \(n) characters" : "Copied \(n) characters", "doc.on.clipboard")
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

    private func toast(_ message: String, _ symbol: String) {
        if settings.showToast { Toast.show(message, symbol: symbol) }
    }

    /// Watches the drag the user makes in the crosshair so we know the exact area
    /// (for "Capture Previous Area") and when they let go (for timing).
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
                // Convert from Cocoa (bottom-left) to screen coordinates (top-left) for screencapture -R.
                let top = NSScreen.screens.first?.frame.maxY ?? 0
                let rect = CGRect(x: min(s.x, p.x), y: top - max(s.y, p.y), width: abs(p.x - s.x), height: abs(p.y - s.y))
                if rect.width > 4, rect.height > 4 { self.lastArea = rect }
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
        if let shortcut {
            // Show the global shortcut on the right like a normal menu shortcut.
            let attributed = NSMutableAttributedString(string: title)
            attributed.append(NSAttributedString(string: "\t" + shortcut.displayString,
                                                 attributes: [.foregroundColor: NSColor.secondaryLabelColor]))
            let style = NSMutableParagraphStyle()
            style.tabStops = [NSTextTab(textAlignment: .right, location: 230)]
            attributed.addAttribute(.paragraphStyle, value: style, range: NSRange(location: 0, length: attributed.length))
            mi.attributedTitle = attributed
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
