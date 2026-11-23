// Draws the TextSnag app icon ("Lift": a page with one highlighted line lifted off it)
// and writes macos/Resources/AppIcon.icns and windows/TextSnag/AppIcon.ico.
//
//   swift scripts/make-icon.swift             both
//   swift scripts/make-icon.swift --windows   only the Windows .ico
//
// Geometry is on Apple's 1024 icon grid (824×824 body, 100 px margin), matching the
// design canvas.
import AppKit

let ink = NSColor(srgbRed: 0x17 / 255.0, green: 0x17 / 255.0, blue: 0x1A / 255.0, alpha: 1)
let paper = NSColor(srgbRed: 0xF7 / 255.0, green: 0xF6 / 255.0, blue: 0xF2 / 255.0, alpha: 1)
let highlight = NSColor(srgbRed: 0xFF / 255.0, green: 0xD6 / 255.0, blue: 0x0A / 255.0, alpha: 1)

/// `fill`: Windows icons use the whole square, so the body is enlarged to cover most of the
/// margin that Apple's grid leaves around it.
func drawIcon(pixels: Int, fill: Bool = false) -> Data {
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: pixels, pixelsHigh: pixels, bitsPerSample: 8,
                               samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB,
                               bytesPerRow: 0, bitsPerPixel: 0)!
    rep.size = NSSize(width: 1024, height: 1024)
    NSGraphicsContext.saveGraphicsState()
    let ctx = NSGraphicsContext(bitmapImageRep: rep)!
    NSGraphicsContext.current = ctx
    // Work in the design's top-left coordinates.
    let flip = NSAffineTransform()
    flip.translateX(by: 0, yBy: 1024)
    flip.scaleX(by: 1, yBy: -1)
    if fill {
        // Show the design's 72…952 region across the full canvas.
        flip.scale(by: 1024 / 880)
        flip.translateX(by: -72, yBy: -72)
    }
    flip.concat()

    func rounded(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ r: CGFloat) -> NSBezierPath {
        NSBezierPath(roundedRect: NSRect(x: x, y: y, width: w, height: h), xRadius: r, yRadius: r)
    }

    // Body with a soft drop shadow, like Apple's own icons.
    NSGraphicsContext.saveGraphicsState()
    let shadow = NSShadow()
    shadow.shadowColor = NSColor.black.withAlphaComponent(0.28)
    shadow.shadowBlurRadius = 24
    shadow.shadowOffset = NSSize(width: 0, height: -10) // flipped: down
    shadow.set()
    paper.setFill()
    rounded(100, 100, 824, 824, 185).fill()
    NSGraphicsContext.restoreGraphicsState()
    ink.withAlphaComponent(0.08).setStroke()
    let edge = rounded(103, 103, 818, 818, 182)
    edge.lineWidth = 6
    edge.stroke()

    // The page's text lines.
    for (y, w, a) in [(300.0, 472.0, 0.18), (384, 400, 0.18), (468, 472, 0.08), (636, 340, 0.18), (720, 420, 0.18)] {
        ink.withAlphaComponent(a).setFill()
        rounded(276, y, w, 40, 20).fill()
    }

    // The lifted line: its shadow on the page, the highlight, and the text on it.
    ink.withAlphaComponent(0.14).setFill()
    rounded(244, 520, 560, 96, 24).fill()
    highlight.setFill()
    rounded(232, 500, 560, 96, 24).fill()
    ink.setFill()
    rounded(276, 528, 420, 40, 20).fill()

    NSGraphicsContext.restoreGraphicsState()
    return rep.representation(using: .png, properties: [:])!
}

let root = URL(fileURLWithPath: CommandLine.arguments[0]).deletingLastPathComponent().deletingLastPathComponent()

/// A Windows .ico: 32-bit uncompressed images up to 64 px (understood everywhere, including
/// .NET's Icon class), PNG for 256 px.
func writeICO(to url: URL) {
    func le16(_ v: Int) -> [UInt8] { [UInt8(v & 0xFF), UInt8((v >> 8) & 0xFF)] }
    func le32(_ v: Int) -> [UInt8] { le16(v & 0xFFFF) + le16((v >> 16) & 0xFFFF) }

    let sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    var images: [[UInt8]] = []
    for size in sizes {
        let png = drawIcon(pixels: size, fill: true)
        if size >= 256 { images.append([UInt8](png)); continue }
        let rep = NSBitmapImageRep(data: png)!
        var dib = le32(40) + le32(size) + le32(size * 2) + le16(1) + le16(32) + le32(0) + le32(size * size * 4)
            + le32(0) + le32(0) + le32(0) + le32(0)
        for y in (0..<size).reversed() { // bottom-up rows, BGRA, straight alpha
            for x in 0..<size {
                let c = rep.colorAt(x: x, y: y)!.usingColorSpace(.deviceRGB)!
                let a = c.alphaComponent
                func channel(_ v: CGFloat) -> UInt8 { UInt8(max(0, min(255, (v * 255).rounded()))) }
                dib += [channel(c.blueComponent), channel(c.greenComponent), channel(c.redComponent), channel(a)]
            }
        }
        let maskRow = ((size + 31) / 32) * 4 // AND mask: all zero, alpha does the work
        dib += [UInt8](repeating: 0, count: maskRow * size)
        images.append(dib)
    }

    var out = le16(0) + le16(1) + le16(sizes.count)
    var offset = 6 + 16 * sizes.count
    for (size, data) in zip(sizes, images) {
        out += [UInt8(size >= 256 ? 0 : size), UInt8(size >= 256 ? 0 : size), 0, 0] + le16(1) + le16(32)
            + le32(data.count) + le32(offset)
        offset += data.count
    }
    for data in images { out += data }
    try! Data(out).write(to: url)
    print("Wrote \(url.path)")
}

writeICO(to: root.appendingPathComponent("windows/TextSnag/AppIcon.ico"))
if CommandLine.arguments.contains("--windows") { exit(0) }

let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("TextSnag.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

for points in [16, 32, 128, 256, 512] {
    try! drawIcon(pixels: points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try! drawIcon(pixels: points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}
try! drawIcon(pixels: 1024).write(to: root.appendingPathComponent("macos/Resources/AppIcon-1024.png"))

let out = root.appendingPathComponent("macos/Resources/AppIcon.icns")
let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", out.path]
try! task.run()
task.waitUntilExit()
print(task.terminationStatus == 0 ? "Wrote \(out.path)" : "iconutil failed")
