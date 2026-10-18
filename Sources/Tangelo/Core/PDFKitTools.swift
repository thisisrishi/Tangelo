import AppKit
import PDFKit

enum PDFKitTools {
    static func open(_ url: URL) throws -> PDFDocument {
        guard let doc = PDFDocument(url: url) else { throw TangeloError.message("The PDF cannot be read or is password-protected.") }
        if doc.isLocked { throw TangeloError.message("This PDF is password-protected.") }
        guard doc.pageCount > 0 else { throw TangeloError.message("This PDF has no pages.") }
        return doc
    }

    static func render(page: PDFPage, scale: CGFloat = 2, background: CGColor = .white) -> CGImage {
        let box = page.bounds(for: .mediaBox)
        let rotated = page.rotation % 180 != 0
        let w = Int((rotated ? box.height : box.width) * scale), h = Int((rotated ? box.width : box.height) * scale)
        let ctx = CGContext(data: nil, width: max(w, 1), height: max(h, 1), bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        ctx.setFillColor(background)
        ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.scaleBy(x: scale, y: scale)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: false)
        page.draw(with: .mediaBox, to: ctx)
        NSGraphicsContext.restoreGraphicsState()
        return ctx.makeImage()!
    }

    /// Lays out rich text over US Letter pages with 1" margins (attachments included).
    static func renderText(_ text: NSAttributedString, to url: URL, pageSize: CGSize = CGSize(width: 612, height: 792)) throws {
        let margin: CGFloat = 72
        let storage = NSTextStorage(attributedString: text)
        let layout = NSLayoutManager()
        storage.addLayoutManager(layout)
        var containers: [NSTextContainer] = []
        let containerSize = CGSize(width: pageSize.width - margin * 2, height: pageSize.height - margin * 2)
        repeat {
            let c = NSTextContainer(size: containerSize)
            layout.addTextContainer(c)
            containers.append(c)
            _ = layout.glyphRange(for: c)
        } while layout.glyphRange(for: containers.last!).upperBound < layout.numberOfGlyphs && containers.count < 5000
        var mediaBox = CGRect(origin: .zero, size: pageSize)
        guard let ctx = CGContext(url as CFURL, mediaBox: &mediaBox, nil) else { throw TangeloError.message("Could not create the PDF.") }
        for c in containers {
            ctx.beginPDFPage(nil)
            NSGraphicsContext.saveGraphicsState()
            let g = NSGraphicsContext(cgContext: ctx, flipped: true)
            NSGraphicsContext.current = g
            ctx.translateBy(x: 0, y: pageSize.height)
            ctx.scaleBy(x: 1, y: -1)
            let range = layout.glyphRange(for: c)
            let origin = CGPoint(x: margin, y: margin)
            layout.drawBackground(forGlyphRange: range, at: origin)
            layout.drawGlyphs(forGlyphRange: range, at: origin)
            NSGraphicsContext.restoreGraphicsState()
            ctx.endPDFPage()
        }
        ctx.closePDF()
    }

    static func createPDF(from images: [URL], to url: URL) throws {
        let doc = PDFDocument()
        for (i, imgURL) in images.enumerated() {
            let cg = try ImageIOKit.load(imgURL)
            let ns = NSImage(cgImage: cg, size: CGSize(width: cg.width, height: cg.height))
            if let page = PDFPage(image: ns) { doc.insert(page, at: i) }
        }
        guard doc.pageCount > 0, doc.write(to: url) else { throw TangeloError.message("Could not create the PDF.") }
    }

    static func merge(_ urls: [URL], to url: URL) throws {
        let out = PDFDocument()
        for u in urls {
            if ConversionFormat.from(u)?.category == .image {
                let cg = try ImageIOKit.load(u)
                if let p = PDFPage(image: NSImage(cgImage: cg, size: CGSize(width: cg.width, height: cg.height))) { out.insert(p, at: out.pageCount) }
                continue
            }
            let doc = try open(u)
            for i in 0..<doc.pageCount { if let p = doc.page(at: i)?.copy() as? PDFPage { out.insert(p, at: out.pageCount) } }
        }
        guard out.write(to: url) else { throw TangeloError.message("Could not create the merged PDF.") }
    }

    /// Splits into single pages, or into chunks when ranges are given ("1-3, 4, 5-9").
    static func split(_ url: URL, ranges: [ClosedRange<Int>]? = nil, into dir: URL) throws -> [URL] {
        let doc = try open(url)
        let groups = ranges ?? (1...doc.pageCount).map { $0...$0 }
        var outs: [URL] = []
        let base = url.deletingPathExtension().lastPathComponent
        for g in groups {
            let part = PDFDocument()
            for i in g where i >= 1 && i <= doc.pageCount {
                if let p = doc.page(at: i - 1)?.copy() as? PDFPage { part.insert(p, at: part.pageCount) }
            }
            guard part.pageCount > 0 else { continue }
            let name = g.count == 1 ? "\(base) page \(g.lowerBound)" : "\(base) pages \(g.lowerBound)-\(g.upperBound)"
            let out = Output.unique(in: dir, base: name, ext: "pdf")
            if part.write(to: out) { outs.append(out) }
        }
        return outs
    }

    static func parseRanges(_ text: String, max: Int) -> [ClosedRange<Int>]? {
        let parts = text.split(separator: ",").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        guard !parts.isEmpty else { return nil }
        var out: [ClosedRange<Int>] = []
        for p in parts {
            let ends = p.split(separator: "-").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
            if ends.count == 1, (1...max).contains(ends[0]) { out.append(ends[0]...ends[0]) }
            else if ends.count == 2, ends[0] <= ends[1], ends[0] >= 1 { out.append(ends[0]...min(ends[1], max)) }
            else { return nil }
        }
        return out
    }

    /// Reorders, rotates, deletes and optionally equalizes page widths.
    static func organize(_ url: URL, order: [Int], rotations: [Int: Int], equalizeWidths: Bool, to out: URL) throws {
        let doc = try open(url)
        let result = PDFDocument()
        let maxWidth = (0..<doc.pageCount).compactMap { doc.page(at: $0)?.bounds(for: .mediaBox).width }.max() ?? 612
        for idx in order {
            guard let p = doc.page(at: idx)?.copy() as? PDFPage else { continue }
            p.rotation = (p.rotation + (rotations[idx] ?? 0)) % 360
            if equalizeWidths {
                let b = p.bounds(for: .mediaBox)
                if b.width > 0, abs(b.width - maxWidth) > 1 {
                    // Render scaled so every page shares the widest page's width.
                    let img = render(page: p, scale: maxWidth / b.width * 2)
                    if let scaled = PDFPage(image: NSImage(cgImage: img, size: CGSize(width: maxWidth, height: b.height * maxWidth / b.width))) {
                        result.insert(scaled, at: result.pageCount); continue
                    }
                }
            }
            result.insert(p, at: result.pageCount)
        }
        guard result.pageCount > 0 else { throw TangeloError.message("Keep at least one page.") }
        guard result.write(to: out) else { throw TangeloError.message("Could not create the organized PDF.") }
    }

    /// Compresses by re-rendering each page as a JPEG at the chosen DPI. Refuses results larger than the original.
    static func compress(_ url: URL, quality: Double, dpi: CGFloat, to out: URL) throws {
        let doc = try open(url)
        let scale = dpi / 72
        var mediaBox = CGRect.zero
        guard let ctx = CGContext(out as CFURL, mediaBox: &mediaBox, nil) else { throw TangeloError.message("Could not create the compressed PDF.") }
        for i in 0..<doc.pageCount {
            guard let page = doc.page(at: i) else { continue }
            var box = page.bounds(for: .mediaBox)
            if page.rotation % 180 != 0 { box = CGRect(x: 0, y: 0, width: box.height, height: box.width) }
            let img = render(page: page, scale: scale)
            let data = NSMutableData()
            if let dest = CGImageDestinationCreateWithData(data, "public.jpeg" as CFString, 1, nil) {
                CGImageDestinationAddImage(dest, ImageIOKit.flatten(img), [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
                CGImageDestinationFinalize(dest)
            }
            guard let provider = CGDataProvider(data: data), let jpeg = CGImage(jpegDataProviderSource: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent) else { continue }
            var pageBox = CGRect(origin: .zero, size: box.size)
            ctx.beginPage(mediaBox: &pageBox)
            ctx.draw(jpeg, in: pageBox)
            ctx.endPage()
        }
        ctx.closePDF()
        // Never hand back something bigger than the original.
        let before = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? Int) ?? 0
        let after = (try? FileManager.default.attributesOfItem(atPath: out.path)[.size] as? Int) ?? 0
        if before > 0, after >= before {
            try? FileManager.default.removeItem(at: out)
            throw TangeloError.message("Compressed output would exceed the original file size. This PDF is already compact.")
        }
    }

    static func exportPages(_ url: URL, format: ConversionFormat, scale: CGFloat = 2, into dir: URL) throws -> [URL] {
        let doc = try open(url)
        let base = url.deletingPathExtension().lastPathComponent
        var outs: [URL] = []
        for i in 0..<doc.pageCount {
            guard let page = doc.page(at: i) else { continue }
            let out = Output.unique(in: dir, base: doc.pageCount == 1 ? base : "\(base) page \(i + 1)", ext: format.fileExtension)
            try ImageIOKit.write(render(page: page, scale: scale), to: out, format: format)
            outs.append(out)
        }
        return outs
    }
}
