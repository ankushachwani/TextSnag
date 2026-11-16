import AppKit
import ImageIO

/// Uses macOS's own `screencapture` tool for the selection UI, the same crosshair as ⌘⇧4.
/// The image goes to a private temp file that is deleted right after it's read; nothing is
/// saved to the Desktop and the clipboard is only touched with the final text.
enum ScreenCapture {
    private static let tool = URL(fileURLWithPath: "/usr/sbin/screencapture")

    /// Shows the crosshair. Calls back on the main queue with nil if the user pressed Esc.
    static func interactive(completion: @escaping (CGImage?) -> Void) {
        run(["-i", "-o"], completion: completion)
    }

    /// Captures a fixed rectangle in global screen coordinates (top-left origin).
    static func rect(_ r: CGRect, completion: @escaping (CGImage?) -> Void) {
        let spec = "\(Int(r.minX)),\(Int(r.minY)),\(Int(r.width)),\(Int(r.height))"
        run(["-R\(spec)"], completion: completion)
    }

    /// The area of the last interactive selection, as recorded by macOS.
    static func lastSelection() -> CGRect? {
        guard let dict = CFPreferencesCopyAppValue("last-selection" as CFString,
                                                   "com.apple.screencapture" as CFString) as? [String: Any],
              let x = (dict["X"] as? NSNumber)?.doubleValue, let y = (dict["Y"] as? NSNumber)?.doubleValue,
              let w = (dict["Width"] as? NSNumber)?.doubleValue, let h = (dict["Height"] as? NSNumber)?.doubleValue,
              w > 1, h > 1
        else { return nil }

        // Coordinates are relative to the display the selection was made on.
        let displayIndex = (CFPreferencesCopyAppValue("last-selection-display" as CFString,
                                                      "com.apple.screencapture" as CFString) as? NSNumber)?.intValue ?? 0
        var ids = [CGDirectDisplayID](repeating: 0, count: 16)
        var count: UInt32 = 0
        CGGetActiveDisplayList(16, &ids, &count)
        let origin = displayIndex < Int(count) ? CGDisplayBounds(ids[displayIndex]).origin : .zero
        return CGRect(x: origin.x + x, y: origin.y + y, width: w, height: h)
    }

    private static func run(_ args: [String], completion: @escaping (CGImage?) -> Void) {
        // TIFF, not PNG: it's lossless too, and skipping PNG compression makes the grab ~4x faster.
        let path = (NSTemporaryDirectory() as NSString).appendingPathComponent("textsnag-\(UUID().uuidString).tiff")
        let process = Process()
        process.executableURL = tool
        process.arguments = ["-x", "-t", "tiff"] + args + [path] // -x: no shutter sound
        process.terminationHandler = { _ in
            let image = loadImage(path)
            try? FileManager.default.removeItem(atPath: path)
            DispatchQueue.main.async { completion(image) }
        }
        do {
            try process.run()
        } catch {
            DispatchQueue.main.async { completion(nil) }
        }
    }

    static func loadImage(_ path: String) -> CGImage? {
        guard let src = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(src, 0, nil)
    }
}
