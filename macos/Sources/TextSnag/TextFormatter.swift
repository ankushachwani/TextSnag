import Foundation

/// User-controlled cleanup applied to recognized text. Every step is optional.
struct FormatOptions {
    var keepLineBreaks = true
    var keepIndentation = true
    var tidyBullets = true
    var fixPunctuation = true
    var straightQuotes = false
    var detectCode = true
    var detectTables = true
}

/// One line (or table cell) as Vision saw it.
struct TextLine {
    let string: String
    let box: CGRect // normalized 0–1, origin bottom-left
    let confidence: Float
}

enum ContentKind: Equatable {
    case prose
    case code
    case table([[String]])

    var label: String {
        switch self {
        case .prose: return "text"
        case .code: return "code"
        case .table: return "table"
        }
    }
}

enum TextFormatter {
    // MARK: - Detection

    /// A detected table: cell text plus where its columns and rows are (normalized).
    struct TableLayout {
        var rows: [[String]]
        var columns: [ClosedRange<CGFloat>]
        var bands: [ClosedRange<CGFloat>] // each row's vertical extent
    }

    static func table(_ lines: [TextLine]) -> [[String]]? { tableLayout(lines)?.rows }

    /// Looks for rows of separate cells whose columns line up, the way Vision reads a table.
    static func tableLayout(_ lines: [TextLine]) -> TableLayout? {
        guard lines.count >= 4 else { return nil }

        // Group cells into rows by vertical overlap, top row first.
        var rows: [[TextLine]] = []
        for line in lines.sorted(by: { $0.box.midY > $1.box.midY }) {
            if let ref = rows.last?.first,
               abs(ref.box.midY - line.box.midY) < min(ref.box.height, line.box.height) * 0.6 {
                rows[rows.count - 1].append(line)
            } else {
                rows.append([line])
            }
        }
        rows = rows.map { $0.sorted { $0.box.minX < $1.box.minX } }

        let multi = rows.filter { $0.count >= 2 }
        guard multi.count >= 3, Double(multi.count) / Double(rows.count) >= 0.6 else { return nil }

        // Tables have short cells; two-column articles have long ones.
        let words = multi.flatMap { $0 }.map { $0.string.split(separator: " ").count }.sorted()
        guard words[words.count / 2] <= 6 else { return nil }

        // Columns: merge the horizontal extents of cells that overlap across rows.
        var columns: [ClosedRange<CGFloat>] = []
        for cell in multi.flatMap({ $0 }).sorted(by: { $0.box.minX < $1.box.minX }) {
            let range = cell.box.minX...cell.box.maxX
            if let last = columns.last, range.lowerBound <= last.upperBound {
                columns[columns.count - 1] = last.lowerBound...max(last.upperBound, range.upperBound)
            } else {
                columns.append(range)
            }
        }
        guard (2...12).contains(columns.count) else { return nil }

        func column(for cell: TextLine) -> Int {
            let overlaps = columns.map { c in
                max(0, min(c.upperBound, cell.box.maxX) - max(c.lowerBound, cell.box.minX))
            }
            return overlaps.indices.max { overlaps[$0] < overlaps[$1] } ?? 0
        }

        var table: [[String]] = []
        for row in rows {
            var cells = Array(repeating: "", count: columns.count)
            for cell in row {
                let i = column(for: cell)
                cells[i] = cells[i].isEmpty ? cell.string : cells[i] + " " + cell.string
            }
            table.append(cells)
        }
        let used = columns.indices.filter { i in table.filter { !$0[i].isEmpty }.count >= 2 }
        guard used.count >= 2 else { return nil }
        let bands = rows.map { row in (row.map(\.box.minY).min() ?? 0)...(row.map(\.box.maxY).max() ?? 0) }
        return TableLayout(rows: table, columns: columns, bands: bands)
    }

    private static let codeSymbols = try! NSRegularExpression(pattern: #"[{}\[\]();<>=|&]|=>|->|::|//|\+\+|!="#)
    private static let codeWords = try! NSRegularExpression(
        pattern: #"\b(func|def|let|var|const|return|import|from|class|struct|enum|if|else|elif|for|while|switch|case|public|private|static|void|int|string|fn|print|println|console|echo|sudo|npm|pip|git|brew|cd|ls|export|async|await|true|false|null|nil|None|self|this)\b"#)

    /// True when most lines look like source code or shell commands.
    static func looksLikeCode(_ lines: [TextLine]) -> Bool {
        guard !lines.isEmpty else { return false }
        var hits = 0
        for line in lines {
            let s = line.string.trimmingCharacters(in: .whitespaces)
            let range = NSRange(s.startIndex..., in: s)
            let symbols = codeSymbols.numberOfMatches(in: s, range: range)
            let words = codeWords.numberOfMatches(in: s, range: range)
            let shape = s.hasSuffix(";") || s.hasSuffix("{") || s == "}" || s.hasPrefix("$ ") || s.hasPrefix("//")
                || s.hasPrefix("#!") || s.hasPrefix("</") || (s.hasPrefix("#") && words > 0)
            if shape || symbols >= 2 || (symbols >= 1 && words >= 1) { hits += 1 }
        }
        let ratio = Double(hits) / Double(lines.count)
        return lines.count == 1 ? hits == 1 : ratio >= 0.4
    }

    // MARK: - Formatting

    static func format(_ lines: [TextLine], kind: ContentKind, options: FormatOptions) -> String {
        switch kind {
        case .table(let rows):
            return textTable(rows.map { $0.map(fixLookAlikes) })
        case .code:
            return fixLookAlikes(formatCode(lines))
        case .prose:
            return fixLookAlikes(formatProse(lines, options: options))
        }
    }

    /// Code: keep every line and its indentation; never autocorrect, never smart quotes.
    private static func formatCode(_ lines: [TextLine]) -> String {
        let charWidth = estimatedCharWidth(lines)
        let left = lines.map(\.box.minX).min() ?? 0
        return lines.map { line in
            let spaces = charWidth > 0 ? Int(((line.box.minX - left) / charWidth).rounded()) : 0
            return String(repeating: " ", count: max(0, spaces)) + codeClean(line.string)
        }.joined(separator: "\n")
    }

    /// Bullet styles by nesting level, the way most apps draw nested lists.
    static let levelBullets = ["•", "◦", "▪"]
    static let indentUnit = "    "

    private static func formatProse(_ lines: [TextLine], options: FormatOptions) -> String {
        let charWidth = max(estimatedCharWidth(lines), 0.004)
        let left = lines.map(\.box.minX).min() ?? 0
        let texts = lines.map { options.tidyBullets ? tidyBullet($0.string) : $0.string }
        let isItem = texts.map(startsListItem)
        let offsets = lines.map { $0.box.minX - left }

        // Nesting levels: group the left edges of list items (or of all lines when there's no
        // list) into steps. Offsets past 40% of the width are a second column, not indentation.
        func levels(of values: [CGFloat]) -> [CGFloat] {
            var steps: [CGFloat] = []
            for v in values.filter({ $0 < 0.4 }).sorted() where steps.last.map({ v - $0 > charWidth * 1.5 }) ?? true {
                steps.append(v)
            }
            return steps
        }
        let hasList = isItem.contains(true)
        let steps = levels(of: zip(offsets, isItem).filter { !hasList || $0.1 }.map(\.0))
        func level(_ offset: CGFloat) -> Int {
            guard offset < 0.4 else { return 0 }
            return steps.lastIndex { $0 <= offset + charWidth * 0.75 } ?? 0
        }

        var out: [String] = []
        var lastItemIndent: String?
        var previous: TextLine?
        for (i, line) in lines.enumerated() {
            defer { previous = line }
            var s = texts[i]
            var indent = ""
            if isItem[i] {
                let lvl = level(offsets[i])
                if options.keepIndentation { indent = String(repeating: indentUnit, count: lvl) }
                // Vision reads ◦ and ▪ as "•"; restore the style from the nesting level.
                if options.tidyBullets, s.hasPrefix("• ") {
                    s = levelBullets[lvl % levelBullets.count] + s.dropFirst(1)
                }
                lastItemIndent = indent
            } else if options.keepIndentation {
                if hasList, let itemIndent = lastItemIndent, offsets[i] > charWidth {
                    indent = itemIndent + "  " // a list item's wrapped line, under its text
                } else if !hasList {
                    indent = String(repeating: indentUnit, count: level(offsets[i]))
                }
            }

            guard let prev = previous, !out.isEmpty else { out.append(indent + s); continue }

            if options.keepLineBreaks || isItem[i] {
                out.append(indent + s)
                continue
            }
            // Paragraph mode: join lines, but a visible gap or a jump back up starts a new paragraph.
            let gap = prev.box.minY - line.box.maxY
            if gap > prev.box.height * 0.8 || line.box.maxY > prev.box.maxY {
                lastItemIndent = nil
                out.append("")
                out.append(s)
            } else if out[out.count - 1].hasSuffix("-"), let first = s.first, first.isLowercase {
                out[out.count - 1].removeLast() // re-join a word hyphenated across lines
                out[out.count - 1] += s
            } else {
                out[out.count - 1] += " " + s
            }
        }

        var text = out.joined(separator: "\n")
        if options.fixPunctuation { text = fixPunctuation(text) }
        if options.straightQuotes { text = straightenQuotes(text) }
        return text
    }

    /// Average width of one character, from lines long enough to measure.
    private static func estimatedCharWidth(_ lines: [TextLine]) -> CGFloat {
        let widths = lines.filter { $0.string.count >= 4 }.map { $0.box.width / CGFloat($0.string.count) }.sorted()
        return widths.isEmpty ? 0 : widths[widths.count / 2]
    }

    /// Text read straight from an app (Direct Text) is already exact, so only layout is
    /// applied. Apps don't report indentation, and list markers often arrive on a line of
    /// their own, so markers are re-attached and the indentation and bullet style are
    /// borrowed from the OCR read of the same area when the lines match up.
    static func formatDirect(_ text: String, ocrText: String, kind: ContentKind, options: FormatOptions) -> String {
        if kind == .code {
            return text.components(separatedBy: "\n").map(codeClean).joined(separator: "\n")
        }

        var lines: [String] = []
        var marker: String?
        for raw in text.components(separatedBy: "\n") {
            let t = raw.trimmingCharacters(in: .whitespaces)
            if isMarkerOnly(t) { marker = t; continue }
            if let m = marker, !t.isEmpty { lines.append(m + " " + t); marker = nil } else { lines.append(t) }
        }
        if options.tidyBullets { lines = lines.map(tidyBullet) }

        let ocrLines = ocrText.components(separatedBy: "\n").filter { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        let appLines = lines.filter { !$0.isEmpty }
        if options.keepLineBreaks, ocrLines.count == appLines.count,
           zip(appLines, ocrLines).allSatisfy({ similarity($0, $1) >= 0.5 }) {
            var j = 0
            lines = lines.map { line in
                guard !line.isEmpty else { return line }
                let ocr = ocrLines[j]
                j += 1
                let indent = options.keepIndentation ? String(ocr.prefix { $0 == " " }) : ""
                let ocrBody = String(ocr.drop { $0 == " " })
                guard let ocrMarker = bulletPrefix(ocrBody) else { return indent + line }
                let body = bulletPrefix(line).map { String(line.dropFirst($0.count)) } ?? line
                return indent + ocrMarker + body
            }
        }

        var out: String
        if options.keepLineBreaks {
            out = lines.joined(separator: "\n")
        } else {
            // Join wrapped lines; blank lines and list items still start a new line.
            out = ""
            for line in lines {
                if out.isEmpty { out = line; continue }
                if line.isEmpty { if !out.hasSuffix("\n\n") { out += "\n\n" }; continue }
                if out.hasSuffix("\n") || startsListItem(line) {
                    out += (out.hasSuffix("\n") ? "" : "\n") + line
                } else {
                    out += " " + line
                }
            }
        }
        if options.straightQuotes { out = straightenQuotes(out) }
        return out
    }

    /// "•", "◦", "1.", "a)" on a line by itself.
    private static func isMarkerOnly(_ s: String) -> Bool {
        s.range(of: #"^([•◦▪●○■□‣⁃∙·\-*–]|\d{1,3}[.)]|[a-zA-Z][.)])$"#, options: .regularExpression) != nil
    }

    /// The bullet and its space at the start of a line ("• ", "◦ "), if any.
    private static func bulletPrefix(_ s: String) -> String? {
        guard let first = s.first, bulletGlyphs.contains(first) || levelBullets.contains(String(first)),
              s.dropFirst().first == " " else { return nil }
        return String(first) + " "
    }

    /// Letter-pair (Dice) similarity between two strings, 0–1.
    static func similarity(_ a: String, _ b: String) -> Double {
        func pairs(_ s: String) -> [String: Int] {
            let chars = Array(s.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) })
            var counts: [String: Int] = [:]
            if chars.count >= 2 { for i in 0..<(chars.count - 1) { counts[String(chars[i]) + String(chars[i + 1]), default: 0] += 1 } }
            return counts
        }
        let x = pairs(a), y = pairs(b)
        let total = x.values.reduce(0, +) + y.values.reduce(0, +)
        guard total > 0 else { return a.lowercased() == b.lowercased() ? 1 : 0 }
        return Double(2 * x.reduce(0) { $0 + min($1.value, y[$1.key] ?? 0) }) / Double(total)
    }

    // MARK: - Look-alike letters

    /// Cyrillic and Greek letters that look identical to Latin ones.
    private static let homoglyphs: [Character: Character] = [
        "а": "a", "е": "e", "о": "o", "р": "p", "с": "c", "у": "y", "х": "x", "і": "i", "ј": "j", "ѕ": "s",
        "ԁ": "d", "һ": "h", "ӏ": "l", "А": "A", "В": "B", "Е": "E", "К": "K", "М": "M", "Н": "H", "О": "O",
        "Р": "P", "С": "C", "Т": "T", "Х": "X", "І": "I", "Ј": "J", "Ѕ": "S", "ο": "o", "ν": "v", "Ο": "O",
        "Α": "A", "Β": "B", "Ε": "E", "Ζ": "Z", "Η": "H", "Ι": "I", "Κ": "K", "Μ": "M", "Ν": "N", "Ρ": "P",
        "Τ": "T", "Υ": "Y", "Χ": "X",
    ]

    /// OCR sometimes returns a Cyrillic "і" inside "Hi". In text that's mostly Latin, swap
    /// look-alike letters back, so words and code stay searchable and runnable. Real
    /// Russian or Greek text (mostly non-Latin) is left alone.
    static func fixLookAlikes(_ text: String) -> String {
        let letters = text.unicodeScalars.filter { CharacterSet.letters.contains($0) }
        guard !letters.isEmpty else { return text }
        let latin = letters.filter { $0.isASCII }.count
        guard Double(latin) / Double(letters.count) >= 0.6 else { return text }
        return String(text.map { homoglyphs[$0] ?? $0 })
    }

    // MARK: - Small text fixes (also used by tests)

    private static let bulletGlyphs: Set<Character> = ["•", "●", "○", "◦", "▪", "▫", "■", "□", "‣", "⁃", "∙", "·",
                                                        "◆", "◇", "►", "▸", "➢", "➤", "✓", "✔", "–", "—", "*", "-"]

    /// Any bullet style at the start of a line becomes "• ".
    static func tidyBullet(_ s: String) -> String {
        let trimmed = s.drop { $0 == " " }
        guard let first = trimmed.first, bulletGlyphs.contains(first) else { return s }
        let rest = trimmed.dropFirst()
        // "-" and "*" only count as bullets when followed by a space ("-5" and "*args" are not).
        if (first == "-" || first == "*" || first == "–" || first == "—"), rest.first != " " { return s }
        guard let text = rest.drop(while: { $0 == " " }).nilIfEmpty else { return s }
        let style = first == "◦" || first == "○" ? "◦" : (first == "▪" || first == "■" ? "▪" : "•")
        return style + " " + text
    }

    private static func startsListItem(_ s: String) -> Bool {
        if s.hasPrefix("• ") { return true }
        return s.range(of: #"^\s*(\d{1,3}|[a-zA-Z])[.)]\s"#, options: .regularExpression) != nil
    }

    static func fixPunctuation(_ text: String) -> String {
        var t = text
        let rules: [(String, String)] = [
            (#"(?<=\S)[ \t]+([,.;:!?%)\]}])"#, "$1"),   // "word ," → "word,"
            (#"([(\[{])[ \t]+(?=\S)"#, "$1"),            // "( word" → "(word"
            (#"(?<=[a-zA-Z]),(?=[a-zA-Z])"#, ", "),      // "one,two" → "one, two" (1,000 untouched)
            (#"(?<=\S) {2,}(?=\S)"#, " "),               // doubled spaces inside a line
            (#"\.\s?\.\s?\.(?!\.)"#, "…"),               // ". . ." → "…"
        ]
        for (pattern, template) in rules {
            t = t.replacingOccurrences(of: pattern, with: template, options: .regularExpression)
        }
        return t
    }

    static func straightenQuotes(_ text: String) -> String {
        var t = text
        for c in ["“", "”", "„", "‟", "″"] { t = t.replacingOccurrences(of: c, with: "\"") }
        for c in ["‘", "’", "‚", "‛", "′", "`"] where c != "`" { t = t.replacingOccurrences(of: c, with: "'") }
        return t
    }

    /// Code characters Vision sometimes returns in "pretty" or full-width form.
    static func codeClean(_ s: String) -> String {
        var t = straightenQuotes(s)
        t = String(String.UnicodeScalarView(t.unicodeScalars.map { u in
            // Full-width ASCII (！ through ～) → normal ASCII; ideographic space → space.
            if (0xFF01...0xFF5E).contains(u.value) { return UnicodeScalar(u.value - 0xFEE0)! }
            if u.value == 0x3000 { return " " }
            return u
        }))
        let swaps: [(String, String)] = [("…", "..."), ("–", "-"), ("—", "--"), ("×", "x"), ("÷", "/"),
                                         ("≠", "!="), ("≤", "<="), ("≥", ">="), ("→", "->"), ("⇒", "=>")]
        for (a, b) in swaps { t = t.replacingOccurrences(of: a, with: b) }
        return t
    }

    // MARK: - Quick-action transforms

    static func oneLine(_ text: String) -> String {
        text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    static func numbers(_ text: String) -> [String] {
        let regex = try! NSRegularExpression(pattern: #"[-+]?\$?\d[\d,]*(?:\.\d+)?%?"#)
        return regex.matches(in: text, range: NSRange(text.startIndex..., in: text)).compactMap {
            Range($0.range, in: text).map { String(text[$0]) }
        }
    }

    /// A table drawn with text, for places that only take plain text (Messages, Slack…):
    /// padded columns with "|" between them. It's also a valid Markdown table, so Notion,
    /// Obsidian, GitHub and other Markdown apps show it as a real table.
    static func textTable(_ rows: [[String]]) -> String {
        guard let header = rows.first, !header.isEmpty else { return "" }
        let widths = header.indices.map { c in max(3, rows.map { c < $0.count ? $0[c].count : 0 }.max() ?? 3) }
        func line(_ cells: [String]) -> String {
            "| " + widths.indices.map { c in
                let cell = (c < cells.count ? cells[c] : "").replacingOccurrences(of: "|", with: "/")
                return cell.padding(toLength: widths[c], withPad: " ", startingAt: 0)
            }.joined(separator: " | ") + " |"
        }
        let rule = "| " + widths.map { String(repeating: "-", count: $0) }.joined(separator: " | ") + " |"
        return ([line(header), rule] + rows.dropFirst().map(line)).joined(separator: "\n")
    }

    /// The same table as HTML, which Google Docs, Pages, Notes, Mail, Word, Numbers, Excel
    /// and Sheets paste as a real table.
    static func htmlTable(_ rows: [[String]]) -> String {
        func esc(_ s: String) -> String {
            s.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
                .replacingOccurrences(of: ">", with: "&gt;")
        }
        let cellStyle = "border:1px solid #bbb;padding:4px 8px;text-align:left"
        var html = "<meta charset=\"utf-8\"><table style=\"border-collapse:collapse\">"
        for (r, row) in rows.enumerated() {
            let tag = r == 0 ? "th" : "td"
            html += "<tr>" + row.map { "<\(tag) style=\"\(cellStyle)\">\(esc($0))</\(tag)>" }.joined() + "</tr>"
        }
        return html + "</table>"
    }

    /// "Not a table": each row as an ordinary line of text.
    static func plainRows(_ rows: [[String]]) -> String {
        rows.map { $0.filter { !$0.isEmpty }.joined(separator: " ") }.joined(separator: "\n")
    }

    static func markdownTable(_ rows: [[String]]) -> String {
        guard let header = rows.first else { return "" }
        func line(_ cells: [String]) -> String {
            "| " + cells.map { $0.replacingOccurrences(of: "|", with: "\\|") }.joined(separator: " | ") + " |"
        }
        return ([line(header), line(header.map { _ in "---" })] + rows.dropFirst().map(line)).joined(separator: "\n")
    }
}

private extension Substring {
    var nilIfEmpty: Substring? { isEmpty ? nil : self }
}
