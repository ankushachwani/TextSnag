import Combine
import Foundation
import ServiceManagement

/// Things a global shortcut can trigger.
enum ShortcutAction: String, CaseIterable, Identifiable {
    case capture
    case captureNoLineBreaks
    case capturePrevious
    case toggleAdditive

    var id: String { rawValue }

    var title: String {
        switch self {
        case .capture: return "Start Capture"
        case .captureNoLineBreaks: return "Capture Without Line Breaks"
        case .capturePrevious: return "Capture Previous Area"
        case .toggleAdditive: return "Toggle Additive Clipboard"
        }
    }

    var defaultShortcut: Shortcut? { self == .capture ? .defaultCapture : nil }
}

/// All user settings, persisted in UserDefaults.
final class Settings: ObservableObject {
    static let shared = Settings()
    private let d = UserDefaults.standard

    @Published var keepLineBreaks: Bool { didSet { d.set(keepLineBreaks, forKey: "keepLineBreaks") } }
    @Published var additive: Bool {
        didSet {
            d.set(additive, forKey: "additiveClipboard")
            if !additive { additiveBuffer = "" }
        }
    }
    @Published var additiveBuffer: String { didSet { d.set(additiveBuffer, forKey: "additiveBuffer") } }
    @Published var speak: Bool { didSet { d.set(speak, forKey: "speakAfterCapture") } }
    @Published var openLinks: Bool { didSet { d.set(openLinks, forKey: "openQRLinks") } }
    @Published var showToast: Bool { didSet { d.set(showToast, forKey: "showToast") } }
    @Published var playSound: Bool { didSet { d.set(playSound, forKey: "playSound") } }
    @Published private(set) var shortcuts: [ShortcutAction: Shortcut] = [:]

    /// Fired whenever a shortcut changes so the hot keys get re-registered.
    let shortcutsChanged = PassthroughSubject<Void, Never>()

    private init() {
        keepLineBreaks = d.object(forKey: "keepLineBreaks") as? Bool ?? true
        additive = d.bool(forKey: "additiveClipboard")
        additiveBuffer = d.string(forKey: "additiveBuffer") ?? ""
        speak = d.bool(forKey: "speakAfterCapture")
        openLinks = d.bool(forKey: "openQRLinks")
        showToast = d.object(forKey: "showToast") as? Bool ?? true
        playSound = d.bool(forKey: "playSound")
        for action in ShortcutAction.allCases {
            if let data = d.data(forKey: "shortcut.\(action.rawValue)") {
                shortcuts[action] = try? JSONDecoder().decode(Shortcut?.self, from: data) ?? nil
            } else {
                shortcuts[action] = action.defaultShortcut
            }
        }
    }

    func shortcut(for action: ShortcutAction) -> Shortcut? { shortcuts[action] }

    func setShortcut(_ shortcut: Shortcut?, for action: ShortcutAction) {
        // One combo can only do one thing.
        if let shortcut {
            for (other, sc) in shortcuts where other != action && sc == shortcut {
                shortcuts[other] = nil
                save(nil, for: other)
            }
        }
        shortcuts[action] = shortcut
        save(shortcut, for: action)
        shortcutsChanged.send()
    }

    private func save(_ shortcut: Shortcut?, for action: ShortcutAction) {
        d.set(try? JSONEncoder().encode(shortcut), forKey: "shortcut.\(action.rawValue)")
    }

    var launchAtLogin: Bool {
        get { SMAppService.mainApp.status == .enabled }
        set {
            objectWillChange.send()
            if newValue { try? SMAppService.mainApp.register() } else { try? SMAppService.mainApp.unregister() }
        }
    }
}
