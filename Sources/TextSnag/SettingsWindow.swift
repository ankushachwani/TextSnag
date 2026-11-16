import AppKit
import SwiftUI

final class SettingsWindow {
    static let shared = SettingsWindow()
    private var window: NSWindow?

    func show() {
        if window == nil {
            let w = NSWindow(contentViewController: NSHostingController(rootView: SettingsView()))
            w.title = "TextSnag Settings"
            w.styleMask = [.titled, .closable]
            w.isReleasedWhenClosed = false
            w.center()
            window = w
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }
}

struct SettingsView: View {
    @ObservedObject private var settings = Settings.shared

    var body: some View {
        TabView {
            general.tabItem { Label("General", systemImage: "gearshape") }
            shortcuts.tabItem { Label("Shortcuts", systemImage: "keyboard") }
            about.tabItem { Label("About", systemImage: "info.circle") }
        }
        .padding(20)
        .frame(width: 480)
    }

    private var general: some View {
        Form {
            Section {
                Toggle("Keep line breaks", isOn: $settings.keepLineBreaks)
                Text("Off: lines are joined into paragraphs.").font(.caption).foregroundStyle(.secondary)
                Toggle("Additive clipboard", isOn: $settings.additive)
                HStack {
                    Text("Each capture is added to the previous ones instead of replacing them.")
                        .font(.caption).foregroundStyle(.secondary)
                    Spacer()
                    Button("Clear") { settings.additiveBuffer = "" }
                        .disabled(settings.additiveBuffer.isEmpty)
                }
            }
            Section {
                Toggle("Read text aloud after capture", isOn: $settings.speak)
                Toggle("Open links from QR codes", isOn: $settings.openLinks)
                Toggle("Show “Copied” bubble", isOn: $settings.showToast)
                Toggle("Play sound on capture", isOn: $settings.playSound)
                Toggle("Launch at login", isOn: Binding(get: { settings.launchAtLogin },
                                                        set: { settings.launchAtLogin = $0 }))
            }
        }
        .formStyle(.grouped)
    }

    private var shortcuts: some View {
        Form {
            Section {
                ForEach(ShortcutAction.allCases) { action in
                    HStack {
                        Text(action.title)
                        Spacer()
                        ShortcutRecorder(shortcut: Binding(
                            get: { settings.shortcut(for: action) },
                            set: { settings.setShortcut($0, for: action) }))
                            .frame(width: 150, height: 24)
                    }
                }
            } footer: {
                Text("Click a box, then press the keys. Use ⌘, ⌥ or ⌃ (or an F-key). Esc cancels, ⌫ clears.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var about: some View {
        VStack(spacing: 8) {
            Image(systemName: "text.viewfinder").font(.system(size: 40))
            Text("TextSnag").font(.title2.bold())
            Text("Version \(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev")")
                .foregroundStyle(.secondary)
            Text("Free, open-source screen text capture for macOS.\nEverything runs on your Mac; nothing is uploaded.")
                .multilineTextAlignment(.center).font(.callout)
        }
        .frame(maxWidth: .infinity, minHeight: 200)
    }
}

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
