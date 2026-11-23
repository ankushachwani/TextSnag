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
            var options = FormatOptions()
            options.keepLineBreaks = !args.contains("--no-line-breaks")
            let result = OCR.recognize(image, options: options)
            print(args.contains("--raw") ? result.raw : result.combined)
            fputs(String(format: "(%@, recognized in %.3fs)\n", result.kind.label, Date().timeIntervalSince(start)), stderr)
            return result.isEmpty ? 1 : 0
        case "--lines":
            // Debug: what Vision returned, line by line, with left edges.
            guard args.count >= 2, let image = ScreenCapture.loadImage(args[1]) else { return 2 }
            OCR.warmUp()
            for l in OCR.readLines(image, correction: true) {
                print(String(format: "x=%.3f w=%.3f h=%.3f  %@", l.box.minX, l.box.width, l.box.height, l.string))
            }
            return 0
        case "--write-list-sample":
            let lines = ["• Fruit", "◦ Apples and pears", "◦ Bananas", "• Vegetables", "◦ Carrots", "▪ Organic only",
                         "1. First step", "2. Second step"]
            let img = OCR.sampleImage(lines, scale: 2, indents: [0, 4, 4, 0, 4, 8, 0, 0])
            let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: args[1]) as CFURL, "public.png" as CFString, 1, nil)!
            CGImageDestinationAddImage(dest, img, nil)
            return CGImageDestinationFinalize(dest) ? 0 : 1
        case "--selftest":
            return selfTest()
        case "--paste-test":
            // Copies through the paste-detecting clipboard, then pastes from another process
            // (pbpaste) and checks TextSnag noticed. Restores the clipboard afterwards.
            let saved = NSPasteboard.general.string(forType: .string)
            var pasted = false
            let watcher = PasteWatcher(text: "TextSnag paste test\n    ◦ indented") { pasted = true }
            let item = NSPasteboardItem()
            item.setDataProvider(watcher, forTypes: [.string])
            NSPasteboard.general.clearContents()
            NSPasteboard.general.writeObjects([item])
            CFRunLoopRunInMode(.defaultMode, 1.0, false) // a real paste comes after a moment
            let pipe = Pipe()
            let pb = Process()
            pb.executableURL = URL(fileURLWithPath: "/usr/bin/pbpaste")
            pb.standardOutput = pipe
            try? pb.run()
            let deadline = Date().addingTimeInterval(5)
            while pb.isRunning && Date() < deadline { CFRunLoopRunInMode(.defaultMode, 0.05, false) }
            CFRunLoopRunInMode(.defaultMode, 0.2, false)
            let got = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
            NSPasteboard.general.clearContents()
            if let saved { NSPasteboard.general.setString(saved, forType: .string) }
            let ok = pasted && got == "TextSnag paste test\n    ◦ indented"
            print("\(ok ? "PASS" : "FAIL")  paste detected: \(pasted), pasted text intact: \(got == "TextSnag paste test\n    ◦ indented")")
            return ok ? 0 : 1
        case "--capture-test":
            // End-to-end without the mouse: grab a screen area with screencapture, read it,
            // report size and timing only (the text itself isn't printed).
            OCR.warmUp()
            let start = Date()
            var output: (Int, Double, Double)?
            ScreenCapture.rect(CGRect(x: 0, y: 0, width: 900, height: 600)) { image in
                let grabbed = Date().timeIntervalSince(start)
                let text = image.map { OCR.recognize($0).combined } ?? ""
                output = (text.count, grabbed, Date().timeIntervalSince(start))
                CFRunLoopStop(CFRunLoopGetMain())
            }
            CFRunLoopRunInMode(.defaultMode, 30, false)
            guard let (chars, grab, total) = output else { print("FAIL  capture timed out"); return 1 }
            print(String(format: "%@  captured screen area: %d characters, grab %.3fs, total %.3fs",
                         chars > 0 ? "PASS" : "FAIL", chars, grab, total))
            return chars > 0 ? 0 : 1
        default:
            fputs("usage: TextSnag [--ocr <image> [--no-line-breaks] | --selftest | --capture-test]\n", stderr)
            return 2
        }
    }

    private static func selfTest() -> Int32 {
        var failures = 0
        func check(_ name: String, _ ok: Bool, _ detail: String = "") {
            print("\(ok ? "PASS" : "FAIL")  \(name)\(detail.isEmpty ? "" : "  — \(detail)")")
            if !ok { failures += 1 }
        }
        func show(_ s: String) -> String { s.replacingOccurrences(of: "\n", with: "⏎").replacingOccurrences(of: "\t", with: "⇥") }

        var t = Date()
        OCR.warmUp()
        print(String(format: "      warm-up %.2fs (slow only the first time this app ever runs)", Date().timeIntervalSince(t)))

        // Prose
        print("\n— Text")
        let img = OCR.sampleImage(["The quick brown fox jumps", "over the lazy dog.", "", "Second paragraph here."])
        t = Date()
        let kept = OCR.recognize(img)
        let elapsed = Date().timeIntervalSince(t)
        check("recognizes text", kept.text.contains("quick brown fox jumps") && kept.text.contains("Second paragraph"), show(kept.text))
        check("keeps line breaks", kept.text.split(separator: "\n").count == 3)
        check("recognition under 0.2s", elapsed < 0.2, String(format: "%.3fs", elapsed))
        check("classified as text", kept.kind == .prose, kept.kind.label)

        var noBreaks = FormatOptions()
        noBreaks.keepLineBreaks = false
        let merged = OCR.recognize(img, options: noBreaks)
        check("merges lines into paragraphs",
              merged.text.contains("jumps over the lazy dog.") && merged.text.contains("\n\nSecond paragraph"), show(merged.text))

        // Same path as a real capture: image written to a temp file, loaded, file deleted, then OCR.
        let tmp = (NSTemporaryDirectory() as NSString).appendingPathComponent("textsnag-selftest.tiff")
        let dest = CGImageDestinationCreateWithURL(URL(fileURLWithPath: tmp) as CFURL, "public.tiff" as CFString, 1, nil)!
        CGImageDestinationAddImage(dest, img, nil)
        CGImageDestinationFinalize(dest)
        let loaded = ScreenCapture.loadImage(tmp)
        try? FileManager.default.removeItem(atPath: tmp)
        let fromFile = loaded.map { OCR.recognize($0) }
        check("reads a capture after its temp file is deleted", fromFile?.text.contains("quick brown fox") == true)

        // Code
        print("\n— Code")
        let mono = NSFont.monospacedSystemFont(ofSize: 24, weight: .regular)
        let codeLines = ["func greet(name: String) {", "if name.isEmpty {", "return", "}", "print(\"Hi, \\(name)!\")", "}"]
        let codeIndents = [0, 4, 8, 4, 4, 0]
        for dark in [false, true] {
            let code = OCR.recognize(OCR.sampleImage(codeLines, scale: 2, font: mono, dark: dark, indents: codeIndents))
            let lines = code.text.components(separatedBy: "\n")
            let indents = lines.map { $0.prefix { $0 == " " }.count }
            let name = dark ? "dark terminal" : "light editor"
            check("\(name): detected as code", code.kind == .code, code.kind.label)
            check("\(name): braces and parentheses intact",
                  code.text.contains("func greet(name: String) {") && code.text.contains("print(\"Hi, \\(name)!\")"), show(code.text))
            check("\(name): indentation kept", indents == codeIndents, indents.description)
        }

        // Tables
        print("\n— Tables")
        let tableImage = renderTable([["Name", "Qty", "Price"], ["Apples", "3", "$1.20"], ["Bananas", "12", "$0.50"],
                                      ["Cherries", "1", "$4.00"]])
        let table = OCR.recognize(tableImage)
        if case .table(let rows) = table.kind {
            check("detected as table", true, "\(rows.count) rows × \(rows.first?.count ?? 0) columns")
            check("rows and columns", rows.count == 4 && rows.allSatisfy { $0.count == 3 } && rows[2][0] == "Bananas"
                  && rows[3][1] == "1", show(table.text))
            check("plain text drawn as a table", table.text.hasPrefix("| Name     | Qty | Price |\n| -------- | --- | ----- |"), show(table.text))
            check("HTML table for Docs, Notes, Sheets…", TextFormatter.htmlTable(rows).contains("<th style=\"border:1px solid #bbb;padding:4px 8px;text-align:left\">Name</th>"))
            check("Not a Table action", TextFormatter.plainRows(rows).hasPrefix("Name Qty Price\nApples 3 $1.20"), show(TextFormatter.plainRows(rows)))
        } else {
            check("detected as table", false, "\(table.kind.label): \(show(table.text))")
        }
        let paragraph = OCR.recognize(OCR.sampleImage(["A normal paragraph of text that wraps", "onto a second line and then",
                                                       "a third, with no columns at all."]))
        check("ordinary text is not mistaken for a table", paragraph.kind == .prose, paragraph.kind.label)

        // Lists
        print("\n— Lists")
        let listLines = ["• Fruit", "◦ Apples and pears", "◦ Bananas", "• Vegetables", "◦ Carrots", "▪ Organic only",
                         "1. First step", "2. Second step"]
        let listIndents = [0, 4, 4, 0, 4, 8, 0, 0]
        let expectedList = "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas\n• Vegetables\n    ◦ Carrots\n        ▪ Organic only\n1. First step\n2. Second step"
        let list = OCR.recognize(OCR.sampleImage(listLines, scale: 2, indents: listIndents))
        check("bullets, styles and nesting kept", list.text == expectedList, show(list.text))
        let direct = TextFormatter.formatDirect("•\nFruit\n•\nApples and pears\n•\nBananas", ocrText: "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas",
                                                kind: .prose, options: FormatOptions())
        check("Direct Text keeps bullets and indentation", direct == "• Fruit\n    ◦ Apples and pears\n    ◦ Bananas", show(direct))

        let sampleRows = [["Name", "Qty"], ["Apples", "3"], ["Bananas", "12"]]
        let html = TextFormatter.htmlTable(sampleRows).data(using: .utf8)!
        let parsed = try? NSAttributedString(data: html, options: [.documentType: NSAttributedString.DocumentType.html,
                                                                  .characterEncoding: String.Encoding.utf8.rawValue],
                                             documentAttributes: nil)
        let cells = parsed.map { str -> Int in
            var blocks = Set<ObjectIdentifier>()
            str.enumerateAttribute(.paragraphStyle, in: NSRange(location: 0, length: str.length)) { value, _, _ in
                for block in (value as? NSParagraphStyle)?.textBlocks ?? [] where block is NSTextTableBlock {
                    blocks.insert(ObjectIdentifier(block))
                }
            }
            return blocks.count
        } ?? 0
        check("macOS reads the copied table as a real table", cells == 6, "\(cells) table cells")

        // Formatting rules
        print("\n— Formatting")
        check("tidy bullets", ["● one", "▪ two", "- three", "* four", "•five", "◦ six"].map(TextFormatter.tidyBullet)
              == ["• one", "▪ two", "• three", "• four", "• five", "◦ six"])
        check("hyphens and numbers are not bullets", TextFormatter.tidyBullet("-5 degrees") == "-5 degrees"
              && TextFormatter.tidyBullet("*args") == "*args")
        let fixed = TextFormatter.fixPunctuation("Hello , world ( yes ) one,two 1,000  done . . .")
        check("fix punctuation spacing", fixed == "Hello, world (yes) one, two 1,000 done…", fixed)
        check("look-alike letters", TextFormatter.fixLookAlikes("print(\"Hі\") Неllo") == "print(\"Hi\") Hello"
              && TextFormatter.fixLookAlikes("Привет мир") == "Привет мир")
        check("straight quotes", TextFormatter.straightenQuotes("“Hi” it’s") == "\"Hi\" it's")
        check("code cleanup", TextFormatter.codeClean("ｘ＝１ → “a” — b") == "x=1 -> \"a\" -- b",
              TextFormatter.codeClean("ｘ＝１ → “a” — b"))

        // Quick actions
        print("\n— Quick actions")
        check("one line", TextFormatter.oneLine("a\nb  c\n\nd") == "a b c d")
        check("numbers only", TextFormatter.numbers("Total: $1,200.50 for 3 items (15%)") == ["$1,200.50", "3", "15%"])

        // Direct Text agreement check
        print("\n— Direct Text")
        check("accepts app text that matches the screen",
              DirectText.agrees(direct: "Are you sure you want to delete “Report.pdf”?", ocr: "Are you sure you want to delete \"Report.pdf\"?"))
        check("tolerates OCR mistakes", DirectText.agrees(direct: "Serial number: C02XK1ZTJGH5", ocr: "Serial nurnber: C02XK1ZTJGH5"))
        check("rejects hidden extra text", !DirectText.agrees(direct: "Visible line\nHidden tooltip text with many more words here",
                                                            ocr: "Visible line"))
        check("Accessibility access", true, DirectText.isTrusted ? "granted" : "not granted (Direct Text falls back to OCR)")

        // App
        print("\n— App")
        let sc = Shortcut.defaultCapture
        check("default shortcut is ⌥⌘5", sc.displayString == "⌥⌘5", sc.displayString)
        _ = NSApplication.shared
        let menu = NSMenu()
        let delegate = AppDelegate()
        delegate.menuNeedsUpdate(menu)
        let titles = menu.items.filter { !$0.isSeparatorItem }.map(\.title)
        check("menu bar menu is compact", menu.size.width < 300,
              String(format: "%.0fpt wide, %d items", menu.size.width, titles.count))
        check("reads QR code", OCR.recognize(renderQR("https://example.com/textsnag")).codes == ["https://example.com/textsnag"])

        print(failures == 0 ? "\nAll checks passed." : "\n\(failures) check(s) failed.")
        return failures == 0 ? 0 : 1
    }

    private static func renderTable(_ rows: [[String]]) -> CGImage {
        let font = NSFont.systemFont(ofSize: 24)
        let columnX: [CGFloat] = [40, 330, 560]
        let rowHeight: CGFloat = 52
        let size = NSSize(width: 800, height: CGFloat(rows.count) * rowHeight + 60)
        let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(size.width * 2), pixelsHigh: Int(size.height * 2),
                                   bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                   colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
        rep.size = size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.white.setFill()
        NSRect(origin: .zero, size: size).fill()
        for (r, row) in rows.enumerated() {
            let y = size.height - 30 - rowHeight - CGFloat(r) * rowHeight
            let attrs: [NSAttributedString.Key: Any] = [.font: r == 0 ? NSFont.boldSystemFont(ofSize: 24) : font,
                                                         .foregroundColor: NSColor.black]
            for (c, cell) in row.enumerated() { (cell as NSString).draw(at: NSPoint(x: columnX[c], y: y), withAttributes: attrs) }
            NSColor(white: 0.85, alpha: 1).setFill()
            NSRect(x: 30, y: y - 10, width: size.width - 60, height: 1).fill()
        }
        NSGraphicsContext.restoreGraphicsState()
        return rep.cgImage!
    }

    private static func renderQR(_ payload: String) -> CGImage {
        let filter = CIFilter(name: "CIQRCodeGenerator")!
        filter.setValue(payload.data(using: .utf8), forKey: "inputMessage")
        let scaled = filter.outputImage!.transformed(by: CGAffineTransform(scaleX: 10, y: 10))
        let padded = scaled.composited(over: CIImage(color: .white).cropped(to: scaled.extent.insetBy(dx: -40, dy: -40)))
        return CIContext().createCGImage(padded, from: padded.extent)!
    }
}
