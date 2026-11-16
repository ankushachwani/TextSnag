import AppKit
import SwiftUI

/// A standard macOS preferences window: icon tabs in the toolbar, no scrolling, and the
/// window resizes to fit each pane.
final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let savedTab = min(UserDefaults.standard.integer(forKey: "settingsTab"), 3) // read before tabs are added
            let tabs = SettingsTabController()
            tabs.tabStyle = .toolbar
            tabs.addPane("General", "gearshape", GeneralPane())
            tabs.addPane("Formatting", "text.alignleft", FormattingPane())
            tabs.addPane("Shortcuts", "keyboard", ShortcutsPane())
            tabs.addPane("About", "info.circle", AboutPane())

            let w = NSWindow(contentViewController: tabs)
            w.styleMask = [.titled, .closable]
            w.toolbarStyle = .preference
            w.isReleasedWhenClosed = false
            tabs.selectedTabViewItemIndex = savedTab
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

private final class SettingsTabController: NSTabViewController {
    func addPane<V: View>(_ title: String, _ symbol: String, _ view: V) {
        let host = NSHostingController(rootView: view)
        host.sizingOptions = [.preferredContentSize]
        host.title = title
        let item = NSTabViewItem(viewController: host)
        item.label = title
        item.image = NSImage(systemSymbolName: symbol, accessibilityDescription: title)
        addTabViewItem(item)
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        fitWindow(animated: false)
    }

    override func tabView(_ tabView: NSTabView, didSelect item: NSTabViewItem?) {
        super.tabView(tabView, didSelect: item)
        UserDefaults.standard.set(selectedTabViewItemIndex, forKey: "settingsTab")
        view.window?.title = item?.label ?? "Settings"
        fitWindow(animated: true)
    }

    /// Resize the window to the selected pane, keeping its top edge in place.
    private func fitWindow(animated: Bool) {
        guard let window = view.window, selectedTabViewItemIndex >= 0,
              let vc = tabViewItems[selectedTabViewItemIndex].viewController else { return }
        window.title = tabViewItems[selectedTabViewItemIndex].label
        let size = vc.view.fittingSize
        let content = window.contentRect(forFrameRect: window.frame)
        var frame = window.frameRect(forContentRect: NSRect(x: content.minX, y: content.maxY - size.height,
                                                            width: size.width, height: size.height))
        frame.origin.x = window.frame.midX - frame.width / 2
        window.setFrame(frame, display: true, animate: animated)
    }
}

// MARK: - Panes

private let labelWidth: CGFloat = 130

/// A right-aligned label column next to controls, like macOS's own settings panes.
private struct Row<Content: View>: View {
    let label: String
    @ViewBuilder let content: Content

    var body: some View {
        GridRow(alignment: .firstTextBaseline) {
            Text(label.isEmpty ? "" : label + ":")
                .frame(width: labelWidth, alignment: .trailing)
            VStack(alignment: .leading, spacing: 6) { content }
        }
    }
}

private struct Hint: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
}

private struct GeneralPane: View {
    @ObservedObject private var settings = Settings.shared

    var body: some View {
        Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 14) {
            Row(label: "Clipboard") {
                Toggle("Additive clipboard", isOn: $settings.additive)
                Hint("Each capture is added after the previous ones.")
                if settings.additive {
                    Button("Clear Additive Clipboard") { settings.additiveBuffer = "" }
                        .disabled(settings.additiveBuffer.isEmpty)
                }
            }
            Divider().gridCellColumns(2)
            Row(label: "After capture") {
                Toggle("Show “Copied” bubble", isOn: $settings.showToast)
                Toggle("Quick actions in the bubble", isOn: $settings.quickActions)
                    .disabled(!settings.showToast)
                Hint("One line, numbers only, Markdown table, open link, call…")
                Toggle("Play sound", isOn: $settings.playSound)
                Toggle("Read text aloud", isOn: $settings.speak)
                Toggle("Open links from QR codes", isOn: $settings.openLinks)
            }
            Divider().gridCellColumns(2)
            Row(label: "Startup") {
                Toggle("Launch at login", isOn: Binding(get: { settings.launchAtLogin },
                                                        set: { settings.launchAtLogin = $0 }))
            }
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 24)
        .frame(width: 480, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct FormattingPane: View {
    @ObservedObject private var settings = Settings.shared
    @State private var trusted = DirectText.isTrusted
    private let poll = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 14) {
            Row(label: "Direct Text") {
                Toggle("Copy the app’s own text when possible", isOn: Binding(
                    get: { settings.directText },
                    set: { on in
                        settings.directText = on
                        if on && !DirectText.isTrusted { DirectText.requestAccess() }
                    }))
                Hint("Exact characters, no recognition mistakes, for dialogs, settings and web pages. Images and video still use OCR.")
                if settings.directText && !trusted {
                    HStack(spacing: 8) {
                        Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
                        Text("Needs Accessibility access").font(.callout)
                        Button("Allow…") {
                            DirectText.requestAccess()
                            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
                        }
                        .controlSize(.small)
                    }
                }
            }
            Divider().gridCellColumns(2)
            Row(label: "Detect") {
                Toggle("Code", isOn: $settings.detectCode)
                Hint("Turns off autocorrect, keeps indentation, fixes quotes, handles dark terminals.")
                Toggle("Tables", isOn: $settings.detectTables)
                Hint("Copied as columns, ready to paste into Numbers, Excel or Sheets.")
            }
            Divider().gridCellColumns(2)
            Row(label: "Text") {
                Toggle("Keep line breaks", isOn: $settings.keepLineBreaks)
                Hint("When off, lines are joined into paragraphs.")
                Toggle("Keep indentation", isOn: $settings.keepIndentation)
                Toggle("Tidy bullets", isOn: $settings.tidyBullets)
                Hint("Every bullet style (●, ▪, -, *…) becomes •.")
                Toggle("Fix punctuation spacing", isOn: $settings.fixPunctuation)
                Hint("“word ,word” becomes “word, word”.")
                Toggle("Use straight quotes", isOn: $settings.straightQuotes)
            }
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 24)
        .frame(width: 480, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)
        .onReceive(poll) { _ in trusted = DirectText.isTrusted }
    }
}

private struct ShortcutsPane: View {
    @ObservedObject private var settings = Settings.shared

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 12) {
                ForEach(ShortcutAction.allCases) { action in
                    GridRow(alignment: .center) {
                        Text(action.title + ":")
                            .frame(width: 210, alignment: .trailing)
                        HStack(spacing: 6) {
                            ShortcutRecorder(shortcut: Binding(
                                get: { settings.shortcut(for: action) },
                                set: { settings.setShortcut($0, for: action) }))
                                .frame(width: 140, height: 24)
                            Button {
                                settings.setShortcut(nil, for: action)
                            } label: {
                                Image(systemName: "xmark.circle.fill").foregroundStyle(.secondary)
                            }
                            .buttonStyle(.borderless)
                            .help("Remove shortcut")
                            .opacity(settings.shortcut(for: action) == nil ? 0 : 1)
                        }
                    }
                }
            }
            Hint("Click a box and press the keys you want. Shortcuts need ⌘, ⌥ or ⌃ (or an F-key). Press Esc to cancel.")
                .frame(maxWidth: .infinity, alignment: .center)
                .multilineTextAlignment(.center)
        }
        .padding(.horizontal, 28)
        .padding(.vertical, 24)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

private struct AboutPane: View {
    var body: some View {
        VStack(spacing: 10) {
            if Bundle.main.url(forResource: "AppIcon", withExtension: "icns") != nil {
                Image(nsImage: NSApp.applicationIconImage)
                    .resizable()
                    .frame(width: 72, height: 72)
            } else {
                Image(systemName: "text.viewfinder")
                    .font(.system(size: 52, weight: .light))
                    .frame(width: 72, height: 72)
            }
            Text("TextSnag").font(.title2.bold())
            Text("Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev")")
                .foregroundStyle(.secondary)
            Text("Free, open-source screen text capture for macOS.\nEverything runs on your Mac. Nothing is uploaded.")
                .multilineTextAlignment(.center)
                .font(.callout)
                .padding(.top, 4)
        }
        .padding(28)
        .frame(width: 480)
        .fixedSize(horizontal: false, vertical: true)
    }
}

// MARK: - Shortcut recorder

/// A button that records the next key combination pressed.
struct ShortcutRecorder: NSViewRepresentable {
    @Binding var shortcut: Shortcut?

    func makeNSView(context: Context) -> RecorderButton {
        let b = RecorderButton()
        b.onChange = { shortcut = $0 }
        b.shortcut = shortcut
        return b
    }

    func updateNSView(_ b: RecorderButton, context: Context) {
        b.onChange = { shortcut = $0 }
        if !b.isRecording { b.shortcut = shortcut }
    }
}

final class RecorderButton: NSButton {
    var onChange: ((Shortcut?) -> Void)?
    var shortcut: Shortcut? { didSet { refreshTitle() } }
    private(set) var isRecording = false
    private var monitor: Any?
    private var resignObserver: NSObjectProtocol?

    init() {
        super.init(frame: .zero)
        bezelStyle = .rounded
        setButtonType(.momentaryPushIn)
        target = self
        action = #selector(clicked)
        refreshTitle()
    }

    required init?(coder: NSCoder) { fatalError() }

    @objc private func clicked() { isRecording ? stop() : start() }

    private func start() {
        isRecording = true
        HotKeyCenter.shared.unregisterAll() // so pressing a current shortcut gets recorded, not run
        refreshTitle()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.handle(event)
            return nil
        }
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                                                                object: window, queue: .main) { [weak self] _ in
            self?.stop()
        }
    }

    private func stop() {
        guard isRecording else { return }
        isRecording = false
        if let monitor { NSEvent.removeMonitor(monitor) }
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        monitor = nil
        resignObserver = nil
        refreshTitle()
        Settings.shared.shortcutsChanged.send() // re-register hot keys
    }

    private func handle(_ event: NSEvent) {
        let plain = event.modifierFlags.intersection([.command, .option, .control, .shift]).isEmpty
        if plain && event.keyCode == 53 { stop(); return } // Esc: cancel
        if plain && (event.keyCode == 51 || event.keyCode == 117) { // ⌫ / ⌦: clear
            shortcut = nil
            onChange?(nil)
            stop()
            return
        }
        let sc = Shortcut(event: event)
        guard sc.isValid else { NSSound.beep(); return }
        shortcut = sc
        onChange?(sc)
        stop()
    }

    private func refreshTitle() {
        title = isRecording ? "Press keys…" : (shortcut?.displayString ?? "Record Shortcut")
    }
}
