import AppKit
let out = CommandLine.arguments[1]
try? FileManager.default.createDirectory(atPath: out, withIntermediateDirectories: true)
for (name, px) in [("16x16",16),("16x16@2x",32),("32x32",32),("32x32@2x",64),("128x128",128),("128x128@2x",256),("256x256",256),("256x256@2x",512),("512x512",512),("512x512@2x",1024)] {
    let s = CGFloat(px)
    let img = NSImage(size: NSSize(width: s, height: s), flipped: false) { r in
        let inset = r.insetBy(dx: s * 0.09, dy: s * 0.09)
        let path = NSBezierPath(roundedRect: inset, xRadius: s * 0.2, yRadius: s * 0.2)
        NSGradient(colors: [NSColor(srgbRed: 1, green: 0.6, blue: 0.15, alpha: 1), NSColor(srgbRed: 1, green: 0.32, blue: 0, alpha: 1)])!.draw(in: path, angle: -90)
        // segmented citrus wheel
        let c = NSPoint(x: r.midX, y: r.midY), rad = s * 0.27
        NSColor.white.withAlphaComponent(0.95).setFill()
        NSBezierPath(ovalIn: NSRect(x: c.x - rad, y: c.y - rad, width: rad * 2, height: rad * 2)).fill()
        NSColor(srgbRed: 1, green: 0.45, blue: 0.05, alpha: 1).setFill()
        for i in 0..<8 {
            let a0 = CGFloat(i) * .pi / 4 + 0.07, a1 = CGFloat(i + 1) * .pi / 4 - 0.07
            let p = NSBezierPath()
            p.move(to: c)
            p.appendArc(withCenter: c, radius: rad * 0.82, startAngle: a0 * 180 / .pi, endAngle: a1 * 180 / .pi)
            p.close(); p.fill()
        }
        return true
    }
    let rep = NSBitmapImageRep(data: img.tiffRepresentation!)!
    try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: "\(out)/icon_\(name).png"))
}
