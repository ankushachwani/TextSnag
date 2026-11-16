import AppKit

// Command-line modes (used for testing): `TextSnag --ocr <image>` and `TextSnag --selftest`.
if CommandLine.arguments.count > 1 {
    exit(CLI.run(Array(CommandLine.arguments.dropFirst())))
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu bar only, no Dock icon
app.run()
