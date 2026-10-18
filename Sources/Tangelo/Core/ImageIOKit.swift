import AppKit
import CoreImage
import ImageIO
import PDFKit
import UniformTypeIdentifiers

enum ImageIOKit {
    static let ciContext = CIContext(options: [.useSoftwareRenderer: false])

    static let writableTypes = Set((CGImageDestinationCopyTypeIdentifiers() as? [String]) ?? [])

    /// Whether this Mac can produce the format (natively or through an installed helper).
    static func canWrite(_ format: ConversionFormat) -> Bool {
        switch format {
        case .webp: return Helpers.cwebp != nil
        case .avif: return writableTypes.contains("public.avif") || Helpers.ffmpeg != nil
        case .heic: return writableTypes.contains("public.heic")
        default: return true
        }
    }

    static func load(_ url: URL) throws -> CGImage {
        if let src = CGImageSourceCreateWithURL(url as CFURL, nil),
           let img = CGImageSourceCreateImageAtIndex(src, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary) {
            return applyingOrientation(img, source: src)
        }
        // SVG, PDF and anything else NSImage understands.
        if let ns = NSImage(contentsOf: url) {
            var size = ns.size
            if size.width < 1024 { let s = 1024 / max(size.width, 1); size = CGSize(width: size.width * s, height: size.height * s) }
            var rect = CGRect(origin: .zero, size: size)
            if let cg = ns.cgImage(forProposedRect: &rect, context: nil, hints: nil) { return cg }
        }
        throw TangeloError.message("Could not read the image \(url.lastPathComponent).")
    }

    /// Bakes EXIF orientation into pixels so edits line up with what the user sees.
    private static func applyingOrientation(_ img: CGImage, source: CGImageSource) -> CGImage {
        guard let props = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let raw = props[kCGImagePropertyOrientation] as? UInt32, raw != 1,
              let o = CGImagePropertyOrientation(rawValue: raw) else { return img }
        let ci = CIImage(cgImage: img).oriented(o)
        return ciContext.createCGImage(ci, from: ci.extent) ?? img
    }

    static func properties(_ url: URL) -> [CFString: Any] {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return [:] }
        return CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any] ?? [:]
    }

    static func frameCount(_ url: URL) -> Int {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil) else { return 1 }
        return CGImageSourceGetCount(src)
    }

    /// Writes an image. quality is 0...1 for lossy formats.
    static func write(_ image: CGImage, to url: URL, format: ConversionFormat, quality: Double = 0.85,
                      metadata: [CFString: Any]? = nil) throws {
        switch format {
        case .webp:
            try writeWebP(image, to: url, quality: quality)
            return
        case .avif where !writableTypes.contains("public.avif"):
            try writeViaFFmpeg(image, to: url, args: ["-c:v", "libaom-av1", "-still-picture", "1", "-crf", String(Int((1 - quality) * 50 + 10))])
            return
        case .pdf:
            let ns = NSImage(cgImage: image, size: CGSize(width: image.width, height: image.height))
            guard let page = PDFPage(image: ns) else { throw TangeloError.message("Could not create the PDF page.") }
            let doc = PDFDocument()
            doc.insert(page, at: 0)
            guard doc.write(to: url) else { throw TangeloError.message("Could not write the PDF.") }
            return
        default: break
        }
        var img = image
        if format == .ico { img = squareIcon(image) }
        if format == .jpg || format == .bmp { img = flatten(img) }
        guard let type = format.utType,
              let dest = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil) else {
            throw TangeloError.unsupported("Writing \(format.label) images is not supported.")
        }
        var opts: [CFString: Any] = metadata ?? [:]
        opts[kCGImageDestinationLossyCompressionQuality] = quality
        CGImageDestinationAddImage(dest, img, opts as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { throw TangeloError.message("Could not finish the \(format.label) image.") }
    }

    private static func writeWebP(_ image: CGImage, to url: URL, quality: Double) throws {
        guard let cwebp = Helpers.cwebp else { throw TangeloError.toolMissing("webp") }
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try write(image, to: tmp, format: .png)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: cwebp)
        p.arguments = ["-quiet", "-q", String(Int(quality * 100)), "-metadata", "none", tmp.path, "-o", url.path]
        try p.run(); p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw TangeloError.message("The WebP encoder failed.") }
    }

    private static func writeViaFFmpeg(_ image: CGImage, to url: URL, args: [String]) throws {
        let ffmpeg = try Helpers.requireFFmpeg()
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".png")
        defer { try? FileManager.default.removeItem(at: tmp) }
        try write(image, to: tmp, format: .png)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: ffmpeg)
        p.arguments = ["-y", "-v", "error", "-i", tmp.path] + args + [url.path]
        try p.run(); p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw TangeloError.message("The image encoder failed.") }
    }

    static func resize(_ image: CGImage, maxDimension: Int) -> CGImage {
        let scale = Double(maxDimension) / Double(max(image.width, image.height))
        guard scale < 1 else { return image }
        return resize(image, to: CGSize(width: Double(image.width) * scale, height: Double(image.height) * scale))
    }

    static func resize(_ image: CGImage, to size: CGSize) -> CGImage {
        let w = max(1, Int(size.width.rounded())), h = max(1, Int(size.height.rounded()))
        guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }
        ctx.interpolationQuality = .high
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        return ctx.makeImage() ?? image
    }

    /// ICO needs a square canvas of at most 256 px; letterbox onto transparency.
    static func squareIcon(_ image: CGImage) -> CGImage {
        let side = min(256, max(image.width, image.height))
        let fitted = resize(image, maxDimension: side)
        guard let ctx = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return fitted }
        ctx.draw(fitted, in: CGRect(x: (side - fitted.width) / 2, y: (side - fitted.height) / 2, width: fitted.width, height: fitted.height))
        return ctx.makeImage() ?? fitted
    }

    /// Composites onto white — for formats without alpha.
    static func flatten(_ image: CGImage, background: CGColor = .white) -> CGImage {
        guard image.alphaInfo != .none, image.alphaInfo != .noneSkipLast, image.alphaInfo != .noneSkipFirst,
              let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                  bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return image }
        let r = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        ctx.setFillColor(background); ctx.fill(r); ctx.draw(image, in: r)
        return ctx.makeImage() ?? image
    }

    static func cgImage(from ci: CIImage) -> CGImage? {
        ciContext.createCGImage(ci, from: ci.extent, format: .RGBA8, colorSpace: CGColorSpace(name: CGColorSpace.sRGB))
    }

    /// Re-encodes all frames of an animated GIF (used when stripping metadata or resizing).
    static func writeAnimatedGIF(frames: [(CGImage, Double)], to url: URL, loop: Int = 0) throws {
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.gif.identifier as CFString, frames.count, nil) else {
            throw TangeloError.message("Could not create the GIF.")
        }
        CGImageDestinationSetProperties(dest, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFLoopCount: loop]] as CFDictionary)
        for (img, delay) in frames {
            CGImageDestinationAddImage(dest, img, [kCGImagePropertyGIFDictionary: [kCGImagePropertyGIFDelayTime: delay]] as CFDictionary)
        }
        guard CGImageDestinationFinalize(dest) else { throw TangeloError.message("Could not finish the GIF.") }
    }
}
