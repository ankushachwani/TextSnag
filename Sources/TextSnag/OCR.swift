import AppKit
import CoreImage
import Vision

struct OCRResult {
    var text: String          // formatted, what goes on the clipboard
    var raw: String           // unformatted lines, for the "Original" quick action
    var codes: [String]       // QR / barcode payloads
    var kind: ContentKind
    var isDirectText = false  // true when the text came from the app itself, not OCR

    var isEmpty: Bool { text.isEmpty && codes.isEmpty }

    /// What goes on the clipboard: QR/barcode payloads first, then recognized text.
    var combined: String {
        (codes + (text.isEmpty ? [] : [text])).joined(separator: "\n")
    }
}

enum OCR {
    /// Set once the recognizer has finished warming up in this process.
    private(set) static var isWarm = false

    /// Synchronous. Call from a background queue.
    static func recognize(_ image: CGImage, options: FormatOptions = FormatOptions()) -> OCRResult {
        // QR/barcode detection runs in parallel with text recognition so it adds no time.
        var codes: [String] = []
        let codesDone = DispatchGroup()
        codesDone.enter()
        DispatchQueue.global(qos: .userInitiated).async {
            codes = detectCodes(image)
            codesDone.leave()
        }

        var lines = readLines(image, correction: true)
        var kind = ContentKind.prose
        if options.detectTables, let rows = TextFormatter.table(lines) {
            kind = .table(rows)
        } else if options.detectCode, TextFormatter.looksLikeCode(lines) {
            kind = .code
            lines = rereadAsCode(image, fallback: lines)
        }
        codesDone.wait()

        let text = TextFormatter.format(lines, kind: kind, options: options)
        let raw = lines.map(\.string).joined(separator: "\n")
        // Don't repeat a QR payload that was also read as text.
        let dedupedText = codes.contains(text) ? "" : text
        return OCRResult(text: dedupedText, raw: codes.contains(text) ? "" : raw, codes: codes, kind: kind)
    }

    /// The first time an app uses Apple's text recognizer, macOS prepares the model for that
    /// app, which can take close to a minute on macOS 27. After that it's cached and takes
    /// ~0.1 s. One throwaway pass at launch, on both a small image and a full-size Retina one,
    /// means real captures are always fast.
    static func warmUp() {
        _ = recognize(sampleImage())
        let page = (1...24).map { i in
            i % 6 == 0 ? "" : "Line \(i): The quick brown fox jumps over the lazy dog, 0123456789 (warm-up) — café"
        }
        _ = recognize(sampleImage(page, scale: 2))
        _ = readLines(sampleImage(), correction: false)
        isWarm = true
    }

    // MARK: - Private

    /// Code: language correction turns `cmd` into "cod" and `}` into "M", so read again without
    /// it. Light-on-dark terminals are also tried inverted; the more confident read wins.
    private static func rereadAsCode(_ image: CGImage, fallback: [TextLine]) -> [TextLine] {
        // Both reads run at the same time.
        var best: [TextLine] = []
        var alt: [TextLine]?
        let group = DispatchGroup()
        DispatchQueue.global(qos: .userInitiated).async(group: group) { best = readLines(image, correction: false) }
        if isDark(image), let inverted = invert(image) {
            DispatchQueue.global(qos: .userInitiated).async(group: group) { alt = readLines(inverted, correction: false) }
        }
        group.wait()
        if let alt {
            // Prefer the read that found more lines (lone "}" lines are easy to miss), then confidence.
            if alt.count > best.count || (alt.count == best.count && meanConfidence(alt) > meanConfidence(best)) {
                best = alt
            }
        }
        return best.isEmpty ? fallback : best
    }

    static func readLines(_ image: CGImage, correction: Bool) -> [TextLine] {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = correction
        if #available(macOS 13.0, *) { request.automaticallyDetectsLanguage = true }
        if (try? VNImageRequestHandler(cgImage: image, options: [:]).perform([request])) == nil {
            // Accurate model unavailable: fall back to the fast one rather than returning nothing.
            request.recognitionLevel = .fast
            try? VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        }
        return (request.results ?? []).compactMap { obs in
            guard let c = obs.topCandidates(1).first else { return nil }
            return TextLine(string: c.string, box: obs.boundingBox, confidence: c.confidence)
        }
    }

    private static func detectCodes(_ image: CGImage) -> [String] {
        let request = VNDetectBarcodesRequest()
        try? VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
        var seen = Set<String>()
        return (request.results ?? []).compactMap(\.payloadStringValue).filter { seen.insert($0).inserted }
    }

    private static func meanConfidence(_ lines: [TextLine]) -> Float {
        lines.isEmpty ? 0 : lines.map(\.confidence).reduce(0, +) / Float(lines.count)
    }

    private static func isDark(_ image: CGImage) -> Bool {
        guard let ctx = CGContext(data: nil, width: 8, height: 8, bitsPerComponent: 8, bytesPerRow: 8,
                                  space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue),
              let data = { ctx.draw(image, in: CGRect(x: 0, y: 0, width: 8, height: 8)); return ctx.data }()
        else { return false }
        let pixels = data.bindMemory(to: UInt8.self, capacity: 64)
        let mean = (0..<64).reduce(0) { $0 + Int(pixels[$1]) } / 64
        return mean < 100
    }

    private static func invert(_ image: CGImage) -> CGImage? {
        let filter = CIFilter(name: "CIColorInvert")
        filter?.setValue(CIImage(cgImage: image), forKey: kCIInputImageKey)
        guard let out = filter?.outputImage else { return nil }
        return CIContext(options: [.useSoftwareRenderer: false]).createCGImage(out, from: out.extent)
    }

    /// A small image with lines of text, used for warm-up and self-tests.
    static func sampleImage(_ lines: [String] = ["TextSnag warm up", "Hello world 123"], scale: CGFloat = 1,
                            font: NSFont = .systemFont(ofSize: 28), dark: Bool = false,
                            indents: [Int] = []) -> CGImage {
        let lineHeight = font.pointSize * 1.6
        let size = NSSize(width: 900, height: 60 + CGFloat(lines.count) * lineHeight)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * scale),
                                   pixelsHigh: Int(size.height * scale), bitsPerSample: 8, samplesPerPixel: 4,
                                   hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                                   bytesPerRow: 0, bitsPerPixel: 0)!
        rep.size = size // draw in points; `scale` sets the pixel density
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        (dark ? NSColor(white: 0.1, alpha: 1) : NSColor.white).setFill()
        NSRect(origin: .zero, size: size).fill()
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: dark ? NSColor(white: 0.9, alpha: 1) : NSColor.black]
        let space = ("    " as NSString).size(withAttributes: attrs).width / 4
        for (i, line) in lines.enumerated() {
            let indent = i < indents.count ? CGFloat(indents[i]) * space : 0
            (line as NSString).draw(at: NSPoint(x: 30 + indent, y: size.height - 30 - lineHeight - CGFloat(i) * lineHeight),
                                    withAttributes: attrs)
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }
}
