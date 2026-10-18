import AppKit
import CoreImage
import CoreImage.CIFilterBuiltins
import ImageIO
import PDFKit
import Vision

// MARK: - Adjustments

struct ImageAdjustments: Equatable {
    var exposure: Double = 0       // EV, -2...2
    var contrast: Double = 1       // 0.5...1.5
    var saturation: Double = 1     // 0...2
    var warmth: Double = 0         // -1...1
    var tint: Double = 0           // -1...1
    var highlights: Double = 1     // 0...1
    var shadows: Double = 0        // -1...1
    var sharpness: Double = 0      // 0...2
    var noiseReduction: Double = 0 // 0...0.1
    var vignette: Double = 0       // 0...2
    var grain: Double = 0          // 0...1
    var blackAndWhite = false

    func apply(_ input: CIImage) -> CIImage {
        var img = input
        if exposure != 0 { let f = CIFilter.exposureAdjust(); f.inputImage = img; f.ev = Float(exposure); img = f.outputImage ?? img }
        if contrast != 1 || saturation != 1 || blackAndWhite {
            let f = CIFilter.colorControls(); f.inputImage = img
            f.contrast = Float(contrast); f.saturation = blackAndWhite ? 0 : Float(saturation)
            img = f.outputImage ?? img
        }
        if warmth != 0 || tint != 0 {
            let f = CIFilter.temperatureAndTint(); f.inputImage = img
            f.neutral = CIVector(x: 6500 - CGFloat(warmth) * 2500, y: CGFloat(tint) * 50)
            f.targetNeutral = CIVector(x: 6500, y: 0)
            img = f.outputImage ?? img
        }
        if highlights != 1 || shadows != 0 {
            let f = CIFilter.highlightShadowAdjust(); f.inputImage = img
            f.highlightAmount = Float(highlights); f.shadowAmount = Float(shadows)
            img = f.outputImage ?? img
        }
        if noiseReduction > 0 {
            let f = CIFilter.noiseReduction(); f.inputImage = img; f.noiseLevel = Float(noiseReduction); f.sharpness = 0.4
            img = f.outputImage ?? img
        }
        if sharpness > 0 { let f = CIFilter.sharpenLuminance(); f.inputImage = img; f.sharpness = Float(sharpness); img = f.outputImage ?? img }
        if vignette > 0 { let f = CIFilter.vignette(); f.inputImage = img; f.intensity = Float(vignette); f.radius = 2; img = f.outputImage ?? img }
        if grain > 0, let noise = CIFilter.randomGenerator().outputImage {
            let mono = noise.applyingFilter("CIColorMatrix", parameters: [
                "inputRVector": CIVector(x: 0, y: 1, z: 0, w: 0), "inputGVector": CIVector(x: 0, y: 1, z: 0, w: 0),
                "inputBVector": CIVector(x: 0, y: 1, z: 0, w: 0), "inputAVector": CIVector(x: 0, y: 0, z: 0, w: CGFloat(grain) * 0.25),
            ]).cropped(to: img.extent)
            let f = CIFilter.softLightBlendMode(); f.inputImage = mono; f.backgroundImage = img
            img = f.outputImage ?? img
        }
        return img.cropped(to: input.extent)
    }
}

// MARK: - Annotations

enum AnnotationKind: String, CaseIterable, Identifiable, Codable {
    case arrow, rectangle, ellipse, line, pen, highlight, text, magnifier, number
    var id: String { rawValue }
    var symbol: String {
        switch self {
        case .arrow: return "arrow.up.right"
        case .rectangle: return "rectangle"
        case .ellipse: return "circle"
        case .line: return "line.diagonal"
        case .pen: return "scribble"
        case .highlight: return "highlighter"
        case .text: return "textformat"
        case .magnifier: return "plus.magnifyingglass"
        case .number: return "1.circle"
        }
    }
}

struct Annotation: Identifiable, Equatable {
    var id = UUID()
    var kind: AnnotationKind
    var points: [CGPoint]           // normalized 0...1, top-left origin
    var color: NSColor = .systemRed
    var lineWidth: CGFloat = 0.006  // relative to image width
    var text: String = ""
    var fontSize: CGFloat = 0.04    // relative to image width
    var zoom: CGFloat = 2
    var number: Int = 1

    var start: CGPoint { points.first ?? .zero }
    var end: CGPoint { points.last ?? .zero }
}

enum AnnotationRenderer {
    /// Draws annotations into a top-left-origin context of the given pixel size.
    static func draw(_ annotations: [Annotation], in ctx: CGContext, size: CGSize, source: CGImage?) {
        for a in annotations {
            let p = a.points.map { CGPoint(x: $0.x * size.width, y: $0.y * size.height) }
            guard let first = p.first else { continue }
            let last = p.last!
            let lw = max(1, a.lineWidth * size.width)
            ctx.saveGState()
            ctx.setStrokeColor(a.color.cgColor)
            ctx.setFillColor(a.color.cgColor)
            ctx.setLineWidth(lw)
            ctx.setLineCap(.round); ctx.setLineJoin(.round)
            let rect = CGRect(x: min(first.x, last.x), y: min(first.y, last.y), width: abs(last.x - first.x), height: abs(last.y - first.y))
            switch a.kind {
            case .line:
                ctx.move(to: first); ctx.addLine(to: last); ctx.strokePath()
            case .arrow:
                ctx.move(to: first); ctx.addLine(to: last); ctx.strokePath()
                let angle = atan2(last.y - first.y, last.x - first.x)
                let head = lw * 4.5
                ctx.move(to: last)
                ctx.addLine(to: CGPoint(x: last.x - head * cos(angle - .pi / 7), y: last.y - head * sin(angle - .pi / 7)))
                ctx.addLine(to: CGPoint(x: last.x - head * cos(angle + .pi / 7), y: last.y - head * sin(angle + .pi / 7)))
                ctx.closePath(); ctx.fillPath()
            case .rectangle:
                ctx.addPath(CGPath(roundedRect: rect, cornerWidth: lw, cornerHeight: lw, transform: nil)); ctx.strokePath()
            case .ellipse:
                ctx.strokeEllipse(in: rect)
            case .pen:
                ctx.addLines(between: p); ctx.strokePath()
            case .highlight:
                ctx.setFillColor(a.color.withAlphaComponent(0.35).cgColor)
                ctx.setBlendMode(.multiply)
                ctx.fill(rect)
            case .text:
                drawText(a.text.isEmpty ? "Text" : a.text, at: first, fontSize: a.fontSize * size.width, color: a.color, in: ctx)
            case .number:
                let r = a.fontSize * size.width
                let circle = CGRect(x: first.x - r, y: first.y - r, width: r * 2, height: r * 2)
                ctx.fillEllipse(in: circle)
                let s = NSAttributedString(string: "\(a.number)", attributes: [.font: NSFont.boldSystemFont(ofSize: r * 1.1), .foregroundColor: NSColor.white])
                let b = s.size()
                drawAttributed(s, at: CGPoint(x: first.x - b.width / 2, y: first.y - b.height / 2), in: ctx)
            case .magnifier:
                guard let source else { break }
                let radius = max(rect.width, rect.height) / 2
                guard radius > 2 else { break }
                let center = first
                let circle = CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2)
                let sw = radius * 2 / a.zoom
                // source crop in image pixel space (image is top-left in our drawing coordinates)
                let sx = CGFloat(source.width) / size.width
                let crop = CGRect(x: (center.x - sw / 2) * sx, y: (center.y - sw / 2) * sx, width: sw * sx, height: sw * sx)
                if let piece = source.cropping(to: crop) {
                    ctx.saveGState()
                    ctx.addEllipse(in: circle); ctx.clip()
                    // flip back for image drawing
                    ctx.translateBy(x: 0, y: circle.maxY + circle.minY); ctx.scaleBy(x: 1, y: -1)
                    ctx.draw(piece, in: circle)
                    ctx.restoreGState()
                }
                ctx.setStrokeColor(NSColor.white.cgColor); ctx.setLineWidth(lw * 1.2)
                ctx.setShadow(offset: .zero, blur: lw * 3, color: NSColor.black.withAlphaComponent(0.5).cgColor)
                ctx.strokeEllipse(in: circle)
            }
            ctx.restoreGState()
        }
    }

    static func drawText(_ text: String, at point: CGPoint, fontSize: CGFloat, color: NSColor, in ctx: CGContext) {
        let shadow = NSShadow(); shadow.shadowBlurRadius = fontSize / 8; shadow.shadowColor = NSColor.black.withAlphaComponent(0.35)
        let s = NSAttributedString(string: text, attributes: [.font: NSFont.systemFont(ofSize: fontSize, weight: .bold), .foregroundColor: color, .shadow: shadow])
        drawAttributed(s, at: point, in: ctx)
    }

    private static func drawAttributed(_ s: NSAttributedString, at point: CGPoint, in ctx: CGContext) {
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        s.draw(at: point)
        NSGraphicsContext.restoreGraphicsState()
    }

    static func render(_ image: CGImage, annotations: [Annotation]) -> CGImage {
        let size = CGSize(width: image.width, height: image.height)
        let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.draw(image, in: CGRect(origin: .zero, size: size))
        ctx.translateBy(x: 0, y: size.height); ctx.scaleBy(x: 1, y: -1)
        draw(annotations, in: ctx, size: size, source: image)
        return ctx.makeImage()!
    }
}

// MARK: - Frame (beautify screenshots)

struct GradientPreset: Identifiable, Hashable {
    let id: String
    let colors: [NSColor]
    static func hex(_ v: UInt32) -> NSColor { NSColor(srgbRed: CGFloat(v >> 16 & 0xff) / 255, green: CGFloat(v >> 8 & 0xff) / 255, blue: CGFloat(v & 0xff) / 255, alpha: 1) }
    static let all: [GradientPreset] = [
        .init(id: "citrus", colors: [hex(0xFF8A00), hex(0xFF2E63)]),
        .init(id: "meadow", colors: [hex(0x9BE15D), hex(0x00C9A7)]),
        .init(id: "orchid", colors: [hex(0xDA22FF), hex(0x9733EE)]),
        .init(id: "lagoon", colors: [hex(0x43CEA2), hex(0x185A9D)]),
        .init(id: "sunset", colors: [hex(0xFF5F6D), hex(0xFFC371)]),
        .init(id: "frost", colors: [hex(0xE0EAFC), hex(0xCFDEF3)]),
        .init(id: "violet", colors: [hex(0x8E2DE2), hex(0x4A00E0)]),
        .init(id: "mint", colors: [hex(0xA8FF78), hex(0x78FFD6)]),
        .init(id: "sorbet", colors: [hex(0xFBD3E9), hex(0xBB377D)]),
        .init(id: "midnight", colors: [hex(0x232526), hex(0x414345)]),
        .init(id: "aqua", colors: [hex(0x13547A), hex(0x80D0C7)]),
        .init(id: "emerald", colors: [hex(0x348F50), hex(0x56B4D3)]),
    ]
}

enum CanvasRatio: String, CaseIterable, Identifiable {
    case auto = "Auto", square = "1:1", wide = "16:9", classic = "4:3", portrait = "4:5", story = "9:16"
    var id: String { rawValue }
    var value: CGFloat? {
        switch self {
        case .auto: return nil
        case .square: return 1
        case .wide: return 16 / 9
        case .classic: return 4 / 3
        case .portrait: return 4 / 5
        case .story: return 9 / 16
        }
    }
}

struct FrameOptions: Equatable {
    var preset = GradientPreset.all[0]
    var solidColor: NSColor? = nil
    var backgroundImage: URL? = nil
    var transparent = false
    var padding: CGFloat = 0.08      // fraction of the longer side
    var cornerRadius: CGFloat = 0.02 // fraction of image width
    var shadow: CGFloat = 0.5        // 0...1
    var ratio: CanvasRatio = .auto

    func render(_ image: CGImage) -> CGImage {
        let iw = CGFloat(image.width), ih = CGFloat(image.height)
        let pad = max(iw, ih) * padding
        var cw = iw + pad * 2, ch = ih + pad * 2
        if let r = ratio.value { if cw / ch < r { cw = ch * r } else { ch = cw / r } }
        let ctx = CGContext(data: nil, width: Int(cw), height: Int(ch), bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        let full = CGRect(x: 0, y: 0, width: cw, height: ch)
        if !transparent {
            if let bg = backgroundImage, let img = try? ImageIOKit.load(bg) {
                let s = max(cw / CGFloat(img.width), ch / CGFloat(img.height))
                let w = CGFloat(img.width) * s, h = CGFloat(img.height) * s
                ctx.draw(img, in: CGRect(x: (cw - w) / 2, y: (ch - h) / 2, width: w, height: h))
            } else if let solidColor {
                ctx.setFillColor(solidColor.cgColor); ctx.fill(full)
            } else {
                let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: preset.colors.map(\.cgColor) as CFArray, locations: nil)!
                ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: ch), end: CGPoint(x: cw, y: 0), options: [])
            }
        }
        let rect = CGRect(x: (cw - iw) / 2, y: (ch - ih) / 2, width: iw, height: ih)
        let radius = iw * cornerRadius
        let path = CGPath(roundedRect: rect, cornerWidth: radius, cornerHeight: radius, transform: nil)
        if shadow > 0 {
            ctx.saveGState()
            ctx.setShadow(offset: CGSize(width: 0, height: -iw * 0.01 * shadow), blur: iw * 0.04 * shadow, color: NSColor.black.withAlphaComponent(0.45 * shadow).cgColor)
            ctx.addPath(path); ctx.setFillColor(NSColor.white.cgColor); ctx.fillPath()
            ctx.restoreGState()
        }
        ctx.saveGState(); ctx.addPath(path); ctx.clip(); ctx.draw(image, in: rect); ctx.restoreGState()
        return ctx.makeImage()!
    }
}

// MARK: - Collage

enum CollageStyle: String, CaseIterable, Identifiable {
    case grid = "Grid", horizontal = "Row", vertical = "Column", featured = "Featured"
    var id: String { rawValue }
}

struct CollageOptions: Equatable {
    var style: CollageStyle = .grid
    var spacing: CGFloat = 16
    var padding: CGFloat = 24
    var cornerRadius: CGFloat = 12
    var fill = true
    var background: NSColor = .white
    var cellSize: CGFloat = 800

    func render(_ images: [CGImage]) -> CGImage? {
        guard !images.isEmpty else { return nil }
        var cells: [CGRect] = []
        var size = CGSize.zero
        let n = images.count
        switch style {
        case .grid:
            let cols = Int(ceil(sqrt(Double(n)))), rows = Int(ceil(Double(n) / Double(cols)))
            size = CGSize(width: CGFloat(cols) * cellSize + CGFloat(cols - 1) * spacing + padding * 2,
                          height: CGFloat(rows) * cellSize + CGFloat(rows - 1) * spacing + padding * 2)
            for i in 0..<n {
                let c = i % cols, r = i / cols
                cells.append(CGRect(x: padding + CGFloat(c) * (cellSize + spacing), y: padding + CGFloat(r) * (cellSize + spacing), width: cellSize, height: cellSize))
            }
        case .horizontal:
            let h = cellSize
            var x = padding
            for img in images {
                let w = fill ? h : h * CGFloat(img.width) / CGFloat(img.height)
                cells.append(CGRect(x: x, y: padding, width: w, height: h)); x += w + spacing
            }
            size = CGSize(width: x - spacing + padding, height: h + padding * 2)
        case .vertical:
            let w = cellSize
            var y = padding
            for img in images {
                let h = fill ? w : w * CGFloat(img.height) / CGFloat(img.width)
                cells.append(CGRect(x: padding, y: y, width: w, height: h)); y += h + spacing
            }
            size = CGSize(width: w + padding * 2, height: y - spacing + padding)
        case .featured:
            let big = cellSize * 2 + spacing
            let rest = n - 1
            let rows = max(2, rest)
            let small = rest > 0 ? (big - CGFloat(rows - 1) * spacing) / CGFloat(rows) : 0
            cells.append(CGRect(x: padding, y: padding, width: big, height: big))
            for i in 0..<rest { cells.append(CGRect(x: padding + big + spacing, y: padding + CGFloat(i) * (small + spacing), width: small, height: small)) }
            size = CGSize(width: padding * 2 + big + (rest > 0 ? spacing + small : 0), height: padding * 2 + big)
        }
        guard let ctx = CGContext(data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
        ctx.setFillColor(background.cgColor); ctx.fill(CGRect(origin: .zero, size: size))
        for (img, cellTop) in zip(images, cells) {
            let cell = CGRect(x: cellTop.minX, y: size.height - cellTop.maxY, width: cellTop.width, height: cellTop.height)
            let iw = CGFloat(img.width), ih = CGFloat(img.height)
            let s = fill ? max(cell.width / iw, cell.height / ih) : min(cell.width / iw, cell.height / ih)
            let dw = iw * s, dh = ih * s
            let dest = CGRect(x: cell.midX - dw / 2, y: cell.midY - dh / 2, width: dw, height: dh)
            ctx.saveGState()
            let clipRect = fill ? cell : dest
            ctx.addPath(CGPath(roundedRect: clipRect, cornerWidth: cornerRadius, cornerHeight: cornerRadius, transform: nil)); ctx.clip()
            ctx.interpolationQuality = .high
            ctx.draw(img, in: dest)
            ctx.restoreGState()
        }
        return ctx.makeImage()
    }
}

// MARK: - Redaction

enum RedactionStyle: String, CaseIterable, Identifiable {
    case blur = "Blur", pixelate = "Pixelate", solid = "Solid"
    var id: String { rawValue }
}

struct RedactionRect: Identifiable, Equatable {
    var id = UUID()
    var rect: CGRect                  // normalized, top-left origin
    var timeRange: ClosedRange<Double>? = nil
}

enum Redactor {
    /// Applies redactions to a Core Image image (bottom-left origin extent).
    static func apply(_ image: CIImage, rects: [CGRect], style: RedactionStyle) -> CIImage {
        let e = image.extent
        var out = image
        for r in rects {
            let rect = CGRect(x: e.minX + r.minX * e.width, y: e.minY + (1 - r.maxY) * e.height, width: r.width * e.width, height: r.height * e.height).integral
            let patch: CIImage
            switch style {
            case .solid: patch = CIImage(color: .black).cropped(to: rect)
            case .pixelate:
                let scale = max(8, max(rect.width, rect.height) / 12)
                patch = image.clampedToExtent().applyingFilter("CIPixellate", parameters: ["inputScale": scale, "inputCenter": CIVector(x: rect.midX, y: rect.midY)]).cropped(to: rect)
            case .blur:
                let radius = max(12, max(rect.width, rect.height) / 6)
                patch = image.clampedToExtent().applyingGaussianBlur(sigma: radius).cropped(to: rect)
                    .applyingFilter("CIColorControls", parameters: ["inputSaturation": 0.6])
            }
            out = patch.composited(over: out)
        }
        return out.cropped(to: e)
    }

    /// Finds faces (and optionally text) to pre-fill redaction rectangles.
    static func detect(in image: CGImage, faces: Bool = true, text: Bool = false) throws -> [CGRect] {
        var requests: [VNRequest] = []
        let faceReq = VNDetectFaceRectanglesRequest()
        let textReq = VNRecognizeTextRequest(); textReq.recognitionLevel = .fast
        if faces { requests.append(faceReq) }
        if text { requests.append(textReq) }
        try VNImageRequestHandler(cgImage: image).perform(requests)
        var boxes: [CGRect] = []
        for face in faceReq.results ?? [] {
            let b: CGRect = face.boundingBox
            boxes.append(b.insetBy(dx: -b.width * 0.15, dy: -b.height * 0.2))
        }
        for t in textReq.results ?? [] { boxes.append(t.boundingBox) }
        // Vision uses bottom-left normalized; convert to top-left.
        return boxes.map { (b: CGRect) -> CGRect in
            CGRect(x: max(0, b.minX), y: max(0, 1 - b.maxY), width: min(1, b.width), height: min(1, b.height))
        }
    }
}

// MARK: - One-shot image operations

enum ImageOps {
    static func save(_ image: CGImage, like source: URL, suffix: String, forcePNG: Bool = false) throws -> URL {
        var format = ConversionFormat.from(source) ?? .png
        if forcePNG || ![.jpg, .png, .heic, .tiff, .webp, .avif, .bmp].contains(format) { format = .png }
        let out = Output.url(for: source, ext: format.fileExtension, suffix: suffix)
        try ImageIOKit.write(image, to: out, format: format, quality: 0.92)
        return out
    }

    static func removeBackground(_ url: URL) throws -> URL {
        let cg = try ImageIOKit.load(url)
        let request = VNGenerateForegroundInstanceMaskRequest()
        let handler = VNImageRequestHandler(cgImage: cg)
        try handler.perform([request])
        guard let result = request.results?.first else { throw TangeloError.message("No subject was found in this image.") }
        let mask = try result.generateScaledMaskForImage(forInstances: result.allInstances, from: handler)
        let ci = CIImage(cgImage: cg)
        let maskImage = CIImage(cvPixelBuffer: mask)
        let out = ci.applyingFilter("CIBlendWithMask", parameters: [kCIInputBackgroundImageKey: CIImage.empty(), kCIInputMaskImageKey: maskImage])
        guard let rendered = ImageIOKit.cgImage(from: out.cropped(to: ci.extent)) else { throw TangeloError.message("Could not prepare the image background.") }
        let dest = Output.url(for: url, ext: "png", suffix: "no background")
        try ImageIOKit.write(rendered, to: dest, format: .png)
        return dest
    }

    static func readCodes(_ url: URL) throws -> [String] {
        var images: [CGImage] = []
        if ConversionFormat.from(url) == .pdf, let doc = PDFKitTools.openOptional(url) {
            for i in 0..<min(doc.pageCount, 50) { if let p = doc.page(at: i) { images.append(PDFKitTools.render(page: p, scale: 2)) } }
        } else { images = [try ImageIOKit.load(url)] }
        var out: [String] = []
        for img in images {
            let req = VNDetectBarcodesRequest()
            try VNImageRequestHandler(cgImage: img).perform([req])
            out += (req.results ?? []).compactMap(\.payloadStringValue)
        }
        return Array(NSOrderedSet(array: out)) as? [String] ?? out
    }

    static func extractText(_ url: URL) async throws -> String {
        if ConversionFormat.from(url) == .pdf { return try await DocumentKit.readPDF(url).string }
        return try await OCR.recognize(try ImageIOKit.load(url)).joined(separator: "\n")
    }

    // Compression

    enum ImagePreset: String, CaseIterable, Identifiable {
        case high = "High quality", balanced = "Balanced", small = "Small", tiny = "Smallest"
        var id: String { rawValue }
        var quality: Double { switch self { case .high: return 0.85; case .balanced: return 0.7; case .small: return 0.55; case .tiny: return 0.4 } }
        var maxDimension: Int? { switch self { case .high: return nil; case .balanced: return 3000; case .small: return 2000; case .tiny: return 1280 } }
        var detail: String {
            switch self {
            case .high: return "Better visual quality with moderate file-size savings."
            case .balanced: return "Smaller files with less quality loss."
            case .small: return "Smaller files with more quality loss."
            case .tiny: return "Much smaller files with more visible quality loss."
            }
        }
    }

    static func compress(_ url: URL, preset: ImagePreset?, targetBytes: Int?, keepFormat: Bool) throws -> URL {
        var image = try ImageIOKit.load(url)
        var format = ConversionFormat.from(url) ?? .jpg
        if !keepFormat || ![.jpg, .heic, .webp, .avif, .png].contains(format) { format = .jpg }
        if targetBytes != nil, format == .png { format = .jpg }
        if let d = preset?.maxDimension { image = ImageIOKit.resize(image, maxDimension: d) }
        let out = Output.url(for: url, ext: format.fileExtension, suffix: "compressed")
        if format == .png {
            // PNG is lossless: shrink by quantizing via downscale only.
            try ImageIOKit.write(image, to: out, format: .png)
            return out
        }
        if let target = targetBytes {
            var lo = 0.05, hi = 0.95, best: Data?
            var working = image
            for _ in 0..<3 {
                for _ in 0..<7 {
                    let q = (lo + hi) / 2
                    let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + "." + format.fileExtension)
                    try ImageIOKit.write(working, to: tmp, format: format, quality: q)
                    let d = try Data(contentsOf: tmp); try? FileManager.default.removeItem(at: tmp)
                    if d.count <= target { best = d; lo = q } else { hi = q }
                }
                if best != nil { break }
                working = ImageIOKit.resize(working, maxDimension: Int(Double(max(working.width, working.height)) * 0.7)); lo = 0.05; hi = 0.95
            }
            guard let best else { throw TangeloError.message("Could not reach the target size. Try a larger size.") }
            try best.write(to: out)
        } else {
            try ImageIOKit.write(image, to: out, format: format, quality: preset?.quality ?? 0.7)
        }
        return out
    }
}

extension PDFKitTools {
    static func openOptional(_ url: URL) -> PDFDocument? { try? open(url) }
}

