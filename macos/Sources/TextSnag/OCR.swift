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
        if options.detectTables, let layout = TextFormatter.tableLayout(lines) {
            kind = .table(fillEmptyCells(image, layout))
        } else if options.detectCode, TextFormatter.looksLikeCode(lines) {
            kind = .code
            lines = rereadAsCode(image, fallback: lines)
        } else if options.tidyBullets || options.keepIndentation {
            lines = restoreBullets(image, lines)
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

    // MARK: Bullets

    /// Vision skips small bullet dots, which also loses a list's nesting. Look just left of
    /// where each line's text starts for a small dot or square; if there is one, it's a
    /// bullet, and the line starts at the bullet.
    private static func restoreBullets(_ image: CGImage, _ lines: [TextLine]) -> [TextLine] {
        guard let gray = GrayImage(image) else { return lines }
        let W = CGFloat(gray.width), H = CGFloat(gray.height)
        return lines.map { line in
            let s = line.string.trimmingCharacters(in: .whitespaces)
            if TextFormatter.tidyBullet(s) != s || s.range(of: #"^(\d{1,3}|[a-zA-Z])[.)]\s"#, options: .regularExpression) != nil {
                return line // already starts with a bullet or a number
            }
            let x0 = line.box.minX * W
            let top = (1 - line.box.maxY) * H, bottom = (1 - line.box.minY) * H
            let h = bottom - top
            guard h > 6 else { return line }
            let rowRange = Int(top + 0.12 * h)...Int(bottom - 0.08 * h)
            let colRange = Int(max(0, x0 - 2.2 * h))...Int(max(0, x0 - 0.04 * h))
            guard colRange.count > 2, let blob = gray.nearestBlob(columns: colRange, rows: rowRange, fromRight: true) else { return line }

            let bw = CGFloat(blob.columns.count), bh = CGFloat(blob.rows.count)
            let centerY = CGFloat(blob.rows.lowerBound + blob.rows.upperBound) / 2
            let looksLikeBullet = (0.1 * h...0.6 * h).contains(bw) && (0.1 * h...0.6 * h).contains(bh)
                && (0.5...2.0).contains(bw / bh) && (top + 0.2 * h...bottom - 0.1 * h).contains(centerY)
                && blob.isolatedOnLeft
            guard looksLikeBullet else { return line }
            let minX = CGFloat(blob.columns.lowerBound) / W
            let box = CGRect(x: minX, y: line.box.minY, width: line.box.maxX - minX, height: line.box.height)
            return TextLine(string: "• " + s, box: box, confidence: line.confidence)
        }
    }

    // MARK: Tables

    /// Vision often drops a cell holding a single character ("1", "3"). Re-read each empty
    /// cell on its own, cropped and enlarged.
    private static func fillEmptyCells(_ image: CGImage, _ layout: TextFormatter.TableLayout) -> [[String]] {
        var rows = layout.rows
        var empty: [(Int, Int)] = []
        for r in rows.indices { for c in rows[r].indices where rows[r][c].isEmpty { empty.append((r, c)) } }
        guard !empty.isEmpty, empty.count <= 40 else { return rows }

        let W = CGFloat(image.width), H = CGFloat(image.height)
        var found = Array(repeating: "", count: empty.count)
        DispatchQueue.concurrentPerform(iterations: empty.count) { i in
            let (r, c) = empty[i]
            let col = layout.columns[c], band = layout.bands[r]
            // The column's extent between its neighbours, and the row's band, with some margin.
            let left = c > 0 ? (layout.columns[c - 1].upperBound + col.lowerBound) / 2 : max(0, col.lowerBound - 0.02)
            let right = c + 1 < layout.columns.count ? (col.upperBound + layout.columns[c + 1].lowerBound) / 2 : min(1, col.upperBound + 0.02)
            let pad = (band.upperBound - band.lowerBound) * 0.3
            let rect = CGRect(x: left * W, y: (1 - band.upperBound - pad) * H,
                              width: (right - left) * W, height: (band.upperBound - band.lowerBound + 2 * pad) * H)
                .intersection(CGRect(x: 0, y: 0, width: W, height: H)).integral
            guard rect.width > 2, rect.height > 2, let crop = image.cropping(to: rect), let big = enlarged(crop, by: 3) else { return }
            let text = readLines(big, correction: false).map(\.string).joined(separator: " ")
            found[i] = text.trimmingCharacters(in: .whitespaces)
        }
        for (i, (r, c)) in empty.enumerated() where !found[i].isEmpty { rows[r][c] = found[i] }
        return rows
    }

    /// Scales up and pads with the image's own background, so a lone character is big
    /// enough for Vision to notice.
    private static func enlarged(_ image: CGImage, by factor: CGFloat) -> CGImage? {
        let pad: CGFloat = 24
        let w = Int(CGFloat(image.width) * factor + 2 * pad), h = Int(CGFloat(image.height) * factor + 2 * pad)
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        let corner = GrayImage(image).map { $0.pixel(0, 0) } ?? 255
        ctx.setFillColor(gray: CGFloat(corner) / 255, alpha: 1)
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: pad, y: pad, width: CGFloat(image.width) * factor, height: CGFloat(image.height) * factor))
        return ctx.makeImage()
    }

    /// Apple's newest accurate model (revision 3) sometimes breaks on macOS 27 and fails
    /// every request until a restart ("e5rt … Code 13"); other apps then silently get
    /// worse text. When that happens, use the previous accurate model (revision 2), and only
    /// as a last resort the fast one. Revision 3 is retried every couple of minutes.
    private static let modelLock = NSLock()
    private static var newestBrokenAt: Date?

    static func readLines(_ image: CGImage, correction: Bool) -> [TextLine] {
        modelLock.lock()
        let skipNewest = newestBrokenAt.map { Date().timeIntervalSince($0) < 120 } ?? false
        modelLock.unlock()

        if !skipNewest {
            let request = textRequest(revision: VNRecognizeTextRequestRevision3, correction: correction)
            do {
                try VNImageRequestHandler(cgImage: image, options: [:]).perform([request])
                return lines(from: request)
            } catch {
                debugLog("revision 3 failed: \(error)")
            }
            modelLock.lock()
            newestBrokenAt = Date()
            modelLock.unlock()
        }
        let previous = textRequest(revision: VNRecognizeTextRequestRevision2, correction: correction)
        do {
            try VNImageRequestHandler(cgImage: image, options: [:]).perform([previous])
            return lines(from: previous)
        } catch {
            debugLog("revision 2 failed: \(error)")
        }
        let fast = textRequest(revision: VNRecognizeTextRequestRevision3, correction: correction)
        fast.recognitionLevel = .fast
        try? VNImageRequestHandler(cgImage: image, options: [:]).perform([fast])
        return lines(from: fast)
    }

    private static func textRequest(revision: Int, correction: Bool) -> VNRecognizeTextRequest {
        let request = VNRecognizeTextRequest()
        request.revision = revision
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = correction
        if revision >= VNRecognizeTextRequestRevision3, #available(macOS 13.0, *) {
            request.automaticallyDetectsLanguage = true
        }
        return request
    }

    private static func lines(from request: VNRecognizeTextRequest) -> [TextLine] {
        (request.results ?? []).compactMap { obs in
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

/// An 8-bit grayscale copy of an image, for quick pixel checks.
private struct GrayImage {
    let width: Int, height: Int
    private let pixels: [UInt8]

    init?(_ image: CGImage) {
        let w = image.width, h = image.height
        var buffer = [UInt8](repeating: 0, count: w * h)
        let ok = buffer.withUnsafeMutableBytes { raw -> Bool in
            guard let ctx = CGContext(data: raw.baseAddress, width: w, height: h, bitsPerComponent: 8,
                                      bytesPerRow: w, space: CGColorSpaceCreateDeviceGray(),
                                      bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        guard ok else { return nil }
        width = w
        height = h
        pixels = buffer
    }

    /// Top-left origin.
    func pixel(_ x: Int, _ y: Int) -> UInt8 { pixels[min(max(y, 0), height - 1) * width + min(max(x, 0), width - 1)] }

    struct Blob {
        let columns: ClosedRange<Int>
        let rows: ClosedRange<Int>
        let isolatedOnLeft: Bool // no other ink between it and the left edge of the search area
    }

    /// The run of "ink" columns closest to the right (or left) edge of the area, where ink
    /// is anything clearly different from the area's background.
    func nearestBlob(columns: ClosedRange<Int>, rows: ClosedRange<Int>, fromRight: Bool) -> Blob? {
        let cols = columns.clamped(to: 0...(width - 1)), rws = rows.clamped(to: 0...(height - 1))
        guard cols.count > 1, rws.count > 1 else { return nil }
        // Background: the most common brightness in the area.
        var histogram = [Int](repeating: 0, count: 256)
        for y in rws { for x in cols { histogram[Int(pixel(x, y))] += 1 } }
        let background = histogram.indices.max { histogram[$0] < histogram[$1] } ?? 255
        func ink(_ x: Int, _ y: Int) -> Bool { abs(Int(pixel(x, y)) - background) > 70 }
        let inkColumns = cols.map { x in rws.contains { ink(x, $0) } }

        let order = fromRight ? Array(inkColumns.indices.reversed()) : Array(inkColumns.indices)
        guard let first = order.first(where: { inkColumns[$0] }) else { return nil }
        var last = first
        for i in order.drop(while: { $0 != first }).dropFirst() {
            guard inkColumns[i] else { break }
            last = i
        }
        let lo = min(first, last), hi = max(first, last)
        let x0 = cols.lowerBound + lo, x1 = cols.lowerBound + hi
        let inkRows = rws.filter { y in (x0...x1).contains { ink($0, y) } }
        guard let r0 = inkRows.first, let r1 = inkRows.last else { return nil }
        let others = fromRight ? inkColumns[..<lo].contains(true) : inkColumns[(hi + 1)...].contains(true)
        return Blob(columns: x0...x1, rows: r0...r1, isolatedOnLeft: !others)
    }
}

/// Set TEXTSNAG_DEBUG=1 to print recognition fallbacks to stderr.
func debugLog(_ message: @autoclosure () -> String) {
    if ProcessInfo.processInfo.environment["TEXTSNAG_DEBUG"] != nil { fputs("[TextSnag] \(message())\n", stderr) }
}
