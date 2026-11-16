// Draws the TextSnag app icon ("Lift": a page with one highlighted line lifted off it)
// and writes Resources/AppIcon.icns.
//
//   swift scripts/make-icon.swift
//
// Geometry is on Apple's 1024 icon grid (824×824 body, 100 px margin), matching the
// design canvas.
import AppKit

let ink = NSColor(srgbRed: 0x17 / 255.0, green: 0x17 / 255.0, blue: 0x1A / 255.0, alpha: 1)
let paper = NSColor(srgbRed: 0xF7 / 255.0, green: 0xF6 / 255.0, blue: 0xF2 / 255.0, alpha: 1)
let highlight = NSColor(srgbRed: 0xFF / 255.0, green: 0xD6 / 255.0, blue: 0x0A / 255.0, alpha: 1)

func drawIcon(pixels: Int) -> Data {
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
let iconset = FileManager.default.temporaryDirectory.appendingPathComponent("TextSnag.iconset")
try? FileManager.default.removeItem(at: iconset)
try! FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)

for points in [16, 32, 128, 256, 512] {
    try! drawIcon(pixels: points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
    try! drawIcon(pixels: points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
}
try! drawIcon(pixels: 1024).write(to: root.appendingPathComponent("Resources/AppIcon-1024.png"))

let out = root.appendingPathComponent("Resources/AppIcon.icns")
let task = Process()
task.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
task.arguments = ["-c", "icns", iconset.path, "-o", out.path]
try! task.run()
task.waitUntilExit()
print(task.terminationStatus == 0 ? "Wrote \(out.path)" : "iconutil failed")
