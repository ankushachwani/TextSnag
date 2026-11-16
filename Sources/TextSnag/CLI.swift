import AppKit
import CoreImage

/// Non-UI entry points used for testing.
enum CLI {
    static func run(_ args: [String]) -> Int32 {
        switch args.first {
        case "--ocr":
            guard args.count >= 2, let image = ScreenCapture.loadImage(args[1]) else {
                fputs("usage: TextSnag --ocr <image> [--no-line-breaks]\n", stderr)
                return 2
            }
            OCR.warmUp()
            let start = Date()
            let result = OCR.recognize(image, keepLineBreaks: !args.contains("--no-line-breaks"))
            print(result.combined)
            fputs(String(format: "(recognized in %.3fs)\n", Date().timeIntervalSince(start)), stderr)
            return result.isEmpty ? 1 : 0
        case "--selftest":
            return selfTest()
        default:
            fputs("usage: TextSnag [--ocr <image> [--no-line-breaks] | --selftest]\n", stderr)
            return 2
        }
    }

    private static func selfTest() -> Int32 {
        var failures = 0
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            print("\(ok ? "PASS" : "FAIL")  \(name)\(detail.isEmpty ? "" : "  — \(detail)")")
            if !ok { failures += 1 }
        }

        var t = Date()
        OCR.warmUp()
        print(String(format: "      warm-up %.2fs (slow only the first time this app ever runs)", Date().timeIntervalSince(t)))

        let img = OCR.sampleImage(["The quick brown fox jumps", "over the lazy dog.", "", "Second paragraph here."])
        t = Date()
        let kept = OCR.recognize(img, keepLineBreaks: true)
        let elapsed = Date().timeIntervalSince(t)
        check("recognizes text", kept.text.contains("quick brown fox jumps") && kept.text.contains("Second paragraph"),
              kept.text.replacingOccurrences(of: "\n", with: "⏎"))
        check("keeps line breaks", kept.text.split(separator: "\n").count == 3)
        check("recognition under 0.2s", elapsed < 0.2, String(format: "%.3fs", elapsed))

        let merged = OCR.recognize(img, keepLineBreaks: false)
        check("merges lines into paragraphs",
              merged.text.contains("jumps over the lazy dog.") && merged.text.contains("\n\nSecond paragraph"),
              merged.text.replacingOccurrences(of: "\n", with: "⏎"))

        let qr = OCR.recognize(renderQR("https://example.com/textsnag"), keepLineBreaks: true)
        check("reads QR code", qr.codes == ["https://example.com/textsnag"], qr.codes.description)

        let sc = Shortcut.defaultCapture
        check("default shortcut is ⌥⌘5", sc.displayString == "⌥⌘5", sc.displayString)

        _ = NSApplication.shared
        let menu = NSMenu()
        AppDelegate().menuNeedsUpdate(menu)
        let titles = menu.items.filter { !$0.isSeparatorItem }.map(\.title)
        check("menu bar menu is compact", menu.size.width < 300,
              String(format: "%.0fpt wide, %d items: %@", menu.size.width, titles.count, titles.joined(separator: " · ")))

        print(failures == 0 ? "\nAll checks passed." : "\n\(failures) check(s) failed.")
        return failures == 0 ? 0 : 1
    }

    private static func renderQR(_ payload: String) -> CGImage {
        let filter = CIFilter(name: "CIQRCodeGenerator")!
        filter.setValue(payload.data(using: .utf8), forKey: "inputMessage")
        let scaled = filter.outputImage!.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        let padded = scaled.composited(over: CIImage(color: .white).cropped(to: scaled.extent.insetBy(dx: -40, dy: -40)))
        return CIContext().createCGImage(padded, from: padded.extent)!
    }
}
