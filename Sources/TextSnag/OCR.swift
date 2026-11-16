import AppKit
import Vision

struct OCRResult {
    var text: String
    var codes: [String]

    var isEmpty: Bool { text.isEmpty && codes.isEmpty }

    /// What goes on the clipboard: QR/barcode payloads first, then recognized text.
    var combined: String {
        (codes + (text.isEmpty ? [] : [text])).joined(separator: "\n")
    }
}

enum OCR {
    /// Set once the recognizer has finished its first run in this process.
    private(set) static var isWarm = false

    /// Synchronous. Call from a background queue.
    static func recognize(_ image: CGImage, keepLineBreaks: Bool) -> OCRResult {
        // QR/barcode detection runs in parallel with text recognition so it adds no time.
        var codes: [String] = []
        let codesDone = DispatchGroup()
        codesDone.enter()
        DispatchQueue.global(qos: .userInitiated).async {
            let request = VNDetectBarcodesRequest()
            try? VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
            var seen = Set<String>()
            codes = (request.results ?? []).compactMap(\.payloadStringValue).filter { seen.insert($0).inserted }
            codesDone.leave()
        }

        let textRequest = makeTextRequest(.accurate)
        var lines: [Line] = []
        if (try? VNImageRequestHandler(cgImage: image, options: [:]).perform([textRequest])) != nil {
            lines = extract(textRequest)
        } else {
            // Accurate model unavailable: fall back to the fast one rather than returning nothing.
            let fast = makeTextRequest(.fast)
            try? VNImageRequestHandler(cgImage: image, options: [:]).perform([fast])
            lines = extract(fast)
        }
        isWarm = true
        codesDone.wait()

        let text = format(lines, keepLineBreaks: keepLineBreaks)
        // Don't repeat a QR payload that was also read as text.
        return OCRResult(text: codes.contains(text) ? "" : text, codes: codes)
    }

    /// The first time an app uses Apple's text recognizer, macOS prepares the model for that
    /// app, which can take close to a minute on macOS 27. After that it's cached and takes
    /// ~0.1 s. Doing one throwaway recognition at launch means real captures are always fast.
    static func warmUp() {
        _ = recognize(sampleImage(), keepLineBreaks: true)
    }

    // MARK: - Private

    struct Line {
        let string: String
        let box: CGRect // normalized, origin bottom-left
    }

    private static func makeTextRequest(_ level: VNRequestTextRecognitionLevel) -> VNRecognizeTextRequest {
        let request = VNRecognizeTextRequest()
        request.recognitionLevel = level
        request.usesLanguageCorrection = level == .accurate
        if #available(macOS 13.0, *) { request.automaticallyDetectsLanguage = true }
        return request
    }

    private static func extract(_ request: VNRecognizeTextRequest) -> [Line] {
        (request.results ?? []).compactMap { obs in
            guard let s = obs.topCandidates(1).first?.string else { return nil }
            return Line(string: s, box: obs.boundingBox)
        }
    }

    /// Joins recognized lines. With line breaks off, lines are merged into paragraphs and
    /// a blank line is kept wherever there is a visible vertical gap.
    static func format(_ lines: [Line], keepLineBreaks: Bool) -> String {
        if keepLineBreaks { return lines.map(\.string).joined(separator: "\n") }

        var out = ""
        var previous: Line?
        for line in lines {
            defer { previous = line }
            guard let prev = previous else { out = line.string; continue }

            let gap = prev.box.minY - line.box.maxY
            let newColumnOrParagraph = gap > prev.box.height * 0.8 || line.box.maxY > prev.box.maxY
            if newColumnOrParagraph {
                out += "\n\n" + line.string
            } else if out.hasSuffix("-"), let first = line.string.first, first.isLowercase {
                out.removeLast() // re-join a word hyphenated across lines
                out += line.string
            } else {
                out += " " + line.string
            }
        }
        return out
    }

    /// A small image with a couple of lines of text, used for warm-up and self-tests.
    static func sampleImage(_ lines: [String] = ["TextSnag warm up", "Hello world 123"]) -> CGImage {
        let lineHeight: CGFloat = 44
        let size = NSSize(width: 900, height: 60 + CGFloat(lines.count) * lineHeight)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width), pixelsHigh: Int(size.height),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()
        let attrs: [NSAttributedString.Key: Any] = [.font: NSFont.systemFont(ofSize: 28), .foregroundColor: NSColor.black]
        for (i, line) in lines.enumerated() {
            (line as NSString).draw(at: NSPoint(x: 30, y: size.height - 60 - CGFloat(i) * lineHeight), withAttributes: attrs)
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }
}
