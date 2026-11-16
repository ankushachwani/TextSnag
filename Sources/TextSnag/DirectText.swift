import AppKit
import ApplicationServices

/// Direct Text: when the selected area is part of a real app window (a dialog, a settings
/// page, a web page that blocks selection), read the app's own text through the macOS
/// Accessibility API instead of reading pixels. It's exact, with no recognition mistakes.
///
/// It's only used when it agrees with what OCR saw in the same area, because apps can
/// expose hidden or off-screen text that isn't in your selection.
enum DirectText {
    static var isTrusted: Bool { AXIsProcessTrusted() }

    /// Shows macOS's "allow TextSnag to use Accessibility" prompt.
    static func requestAccess() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    /// Text inside `rect` (global screen coordinates, top-left origin), or nil.
    static func text(in rect: CGRect, timeBudget: TimeInterval = 0.15) -> String? {
        guard isTrusted, rect.width > 6, rect.height > 6 else { return nil }
        let deadline = Date().addingTimeInterval(timeBudget)
        let system = AXUIElementCreateSystemWide()
        AXUIElementSetMessagingTimeout(system, 0.05)
        let ownPID = ProcessInfo.processInfo.processIdentifier

        // Hit-test a grid of points across the selection and collect the distinct elements.
        var elements: [AXUIElement] = []
        var touchedApps = Set<pid_t>()
        let rows = max(2, min(24, Int(rect.height / 10)))
        let cols = rect.width > 240 ? 4 : 2
        outer: for r in 0..<rows {
            for c in 0..<cols {
                if Date() > deadline { break outer }
                let p = CGPoint(x: rect.minX + rect.width * (CGFloat(c) + 0.5) / CGFloat(cols),
                                y: rect.minY + rect.height * (CGFloat(r) + 0.5) / CGFloat(rows))
                var hit: AXUIElement?
                guard AXUIElementCopyElementAtPosition(system, Float(p.x), Float(p.y), &hit) == .success,
                      let element = hit else { continue }
                var pid: pid_t = 0
                AXUIElementGetPid(element, &pid)
                if pid == ownPID { continue }
                if touchedApps.insert(pid).inserted { enableElectronAccessibility(pid) }
                if !elements.contains(where: { CFEqual($0, element) }) { elements.append(element) }
            }
        }

        // Read each element: text views by character range, labels by value/title.
        var pieces: [(frame: CGRect, text: String)] = []
        var seenText = Set<String>()
        for element in elements {
            if Date() > deadline { break }
            guard let frame = frame(of: element) else { continue }
            if let s = textInRange(element, rect: rect) ?? labelText(element, frame: frame, rect: rect) {
                let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
                if !t.isEmpty, seenText.insert(t).inserted { pieces.append((frame, t)) }
            }
        }
        guard !pieces.isEmpty else { return nil }

        // Reading order: top to bottom, then left to right within a row.
        pieces.sort { a, b in
            abs(a.frame.minY - b.frame.minY) > min(a.frame.height, b.frame.height) * 0.5
                ? a.frame.minY < b.frame.minY : a.frame.minX < b.frame.minX
        }
        return pieces.map(\.text).joined(separator: "\n")
    }

    /// True when the app's text and the OCR text describe the same words, allowing for some
    /// OCR mistakes. Guards against hidden, off-screen, or partially selected text.
    static func agrees(direct: String, ocr: String) -> Bool {
        // Compare letter pairs (Dice similarity) rather than whole words, so one misread
        // letter ("nurnber") barely matters but extra hidden text clearly does.
        func pairs(_ s: String) -> [String: Int] {
            let chars = Array(s.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) })
            var counts: [String: Int] = [:]
            if chars.count >= 2 {
                for i in 0..<(chars.count - 1) { counts[String(chars[i]) + String(chars[i + 1]), default: 0] += 1 }
            }
            return counts
        }
        let a = pairs(direct), b = pairs(ocr)
        let total = a.values.reduce(0, +) + b.values.reduce(0, +)
        guard total > 0, !a.isEmpty, !b.isEmpty else { return false }
        let shared = a.reduce(0) { $0 + min($1.value, b[$1.key] ?? 0) }
        return Double(2 * shared) / Double(total) >= 0.8
    }

    // MARK: - Private

    /// Text views and web content: the characters between the first and last positions
    /// inside the selection.
    private static func textInRange(_ element: AXUIElement, rect: CGRect) -> String? {
        var names: CFArray?
        guard AXUIElementCopyParameterizedAttributeNames(element, &names) == .success,
              let list = names as? [String],
              list.contains(kAXRangeForPositionParameterizedAttribute),
              list.contains(kAXStringForRangeParameterizedAttribute) else { return nil }

        func range(at p: CGPoint) -> CFRange? {
            var point = p
            guard let value = AXValueCreate(.cgPoint, &point) else { return nil }
            var out: CFTypeRef?
            guard AXUIElementCopyParameterizedAttributeValue(element, kAXRangeForPositionParameterizedAttribute as CFString,
                                                             value, &out) == .success,
                  let v = out, CFGetTypeID(v) == AXValueGetTypeID() else { return nil }
            var r = CFRange()
            return AXValueGetValue(v as! AXValue, .cfRange, &r) ? r : nil
        }

        let inset: CGFloat = 2
        guard let start = range(at: CGPoint(x: rect.minX + inset, y: rect.minY + inset)),
              let end = range(at: CGPoint(x: rect.maxX - inset, y: rect.maxY - inset)),
              end.location >= start.location else { return nil }
        var span = CFRange(location: start.location, length: end.location + end.length - start.location)
        guard span.length > 0, span.length < 200_000, let spanValue = AXValueCreate(.cfRange, &span) else { return nil }
        var out: CFTypeRef?
        guard AXUIElementCopyParameterizedAttributeValue(element, kAXStringForRangeParameterizedAttribute as CFString,
                                                         spanValue, &out) == .success else { return nil }
        return out as? String
    }

    /// Labels, buttons, cells, static web text: their value or title, if they sit inside the
    /// selection.
    private static func labelText(_ element: AXUIElement, frame: CGRect, rect: CGRect) -> String? {
        let inside = frame.intersection(rect)
        guard !inside.isNull, frame.width * frame.height > 0,
              inside.width * inside.height / (frame.width * frame.height) > 0.5 else { return nil }
        for attribute in [kAXValueAttribute, kAXTitleAttribute, kAXDescriptionAttribute] {
            var value: CFTypeRef?
            if AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success,
               let s = value as? String, !s.isEmpty {
                return s
            }
        }
        return nil
    }

    private static func frame(of element: AXUIElement) -> CGRect? {
        var posRef: CFTypeRef?, sizeRef: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &posRef) == .success,
              AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeRef) == .success,
              let p = posRef, let s = sizeRef else { return nil }
        var point = CGPoint.zero, size = CGSize.zero
        AXValueGetValue(p as! AXValue, .cgPoint, &point)
        AXValueGetValue(s as! AXValue, .cgSize, &size)
        return CGRect(origin: point, size: size)
    }

    /// Electron apps (Slack, VS Code, Discord…) only build their accessibility tree when asked.
    private static func enableElectronAccessibility(_ pid: pid_t) {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
    }
}
