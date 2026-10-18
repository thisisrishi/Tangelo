import Foundation

enum FileTool: String, CaseIterable, Identifiable, Codable {
    // General
    case compress, metadata, batchRename
    // Images
    case editImage, annotateImage, frameImage, cropImage, resizeImage, removeBackground, extractText, redactImage, createCollage, readQRCode
    // Video
    case trimVideo, cropVideo, changeVideoSpeed, muteVideo, joinVideos, splitVideo, videoSnapshots, redactVideo, videoToGIF
    // Audio
    case trimAudio, normalizeAudio, audioChannels, redactAudio, audioToVideo
    // PDF & archives
    case createPDF, mergePDF, splitPDF, organizePDF, extractArchive

    var id: String { rawValue }

    var title: String {
        switch self {
        case .compress: return "Compress"
        case .metadata: return "Metadata"
        case .batchRename: return "Rename"
        case .editImage: return "Adjust"
        case .annotateImage: return "Annotate"
        case .frameImage: return "Frame"
        case .cropImage: return "Crop"
        case .resizeImage: return "Resize"
        case .removeBackground: return "Remove BG"
        case .extractText: return "Copy Text"
        case .redactImage: return "Redact"
        case .createCollage: return "Collage"
        case .readQRCode: return "Read QR"
        case .trimVideo: return "Trim"
        case .cropVideo: return "Crop"
        case .changeVideoSpeed: return "Speed"
        case .muteVideo: return "Mute"
        case .joinVideos: return "Join"
        case .splitVideo: return "Split"
        case .videoSnapshots: return "Snapshots"
        case .redactVideo: return "Redact"
        case .videoToGIF: return "Make GIF"
        case .trimAudio: return "Trim"
        case .normalizeAudio: return "Normalize"
        case .audioChannels: return "Channels"
        case .redactAudio: return "Bleep"
        case .audioToVideo: return "Visualize"
        case .createPDF: return "Make PDF"
        case .mergePDF: return "Merge PDF"
        case .splitPDF: return "Split PDF"
        case .organizePDF: return "Organize"
        case .extractArchive: return "Extract"
        }
    }

    var symbol: String {
        switch self {
        case .compress: return "arrow.down.right.and.arrow.up.left"
        case .metadata: return "info.circle"
        case .batchRename: return "character.cursor.ibeam"
        case .editImage: return "slider.horizontal.3"
        case .annotateImage: return "pencil.tip.crop.circle"
        case .frameImage: return "photo.artframe"
        case .cropImage, .cropVideo: return "crop"
        case .resizeImage: return "arrow.up.left.and.arrow.down.right"
        case .removeBackground: return "person.crop.rectangle"
        case .extractText: return "text.viewfinder"
        case .redactImage, .redactVideo: return "eye.slash"
        case .createCollage: return "square.grid.2x2"
        case .readQRCode: return "qrcode.viewfinder"
        case .trimVideo, .trimAudio: return "timeline.selection"
        case .changeVideoSpeed: return "gauge.with.dots.needle.67percent"
        case .muteVideo: return "speaker.slash"
        case .joinVideos: return "film.stack"
        case .splitVideo: return "rectangle.split.2x1"
        case .videoSnapshots: return "camera.viewfinder"
        case .videoToGIF: return "sparkles.tv"
        case .normalizeAudio: return "dial.medium"
        case .audioChannels: return "hifispeaker.2"
        case .redactAudio: return "waveform.badge.exclamationmark"
        case .audioToVideo: return "waveform.and.magnifyingglass"
        case .createPDF: return "doc.badge.plus"
        case .mergePDF: return "doc.on.doc"
        case .splitPDF: return "scissors"
        case .organizePDF: return "square.grid.3x3.square"
        case .extractArchive: return "archivebox"
        }
    }

    var subtitle: String {
        switch self {
        case .compress: return "Shrink images, video, audio and PDFs — by preset or target size"
        case .metadata: return "View, edit or strip EXIF, GPS and document metadata"
        case .batchRename: return "Rename many files with patterns, numbering and find/replace"
        case .editImage: return "Exposure, contrast, warmth, sharpening and more"
        case .annotateImage: return "Arrows, shapes, text, highlights and magnifiers"
        case .frameImage: return "Place screenshots on gradient backgrounds with shadow"
        case .cropImage: return "Crop to a ratio or exact pixels, batch-friendly"
        case .resizeImage: return "Scale by percent, width, height or longest side"
        case .removeBackground: return "Lift the subject onto a transparent background"
        case .extractText: return "Recognize text in images and PDFs (OCR) and copy it"
        case .redactImage: return "Blur, pixelate or black out regions — auto-detect faces"
        case .createCollage: return "Combine images into grids and layouts"
        case .readQRCode: return "Find QR codes and barcodes and copy their contents"
        case .trimVideo: return "Cut the start and end of a video"
        case .cropVideo: return "Crop the frame of a video"
        case .changeVideoSpeed: return "Speed up or slow down from 0.25× to 4×"
        case .muteVideo: return "Remove the audio track"
        case .joinVideos: return "Combine clips into one video"
        case .splitVideo: return "Cut a video into clips at split points"
        case .videoSnapshots: return "Save frames as PNG images"
        case .redactVideo: return "Blur or cover regions across a time range"
        case .videoToGIF: return "Turn a segment into an animated GIF"
        case .trimAudio: return "Cut audio, optionally trimming silent ends"
        case .normalizeAudio: return "Even out loudness to a target level"
        case .audioChannels: return "Mono/stereo and left/right balance"
        case .redactAudio: return "Bleep or silence parts of audio"
        case .audioToVideo: return "Make a waveform video for sharing audio"
        case .createPDF: return "Combine images into a PDF"
        case .mergePDF: return "Merge PDFs (and images) into one"
        case .splitPDF: return "Split into pages or page ranges"
        case .organizePDF: return "Reorder, rotate and delete pages"
        case .extractArchive: return "Unpack zip, rar, 7z, tar, gz and more"
        }
    }

    /// Tools that run straight away without opening an editor.
    var runsImmediately: Bool {
        [.muteVideo, .extractArchive, .createPDF, .mergePDF, .readQRCode, .removeBackground, .extractText].contains(self)
    }

    func applies(to categories: Set<FileCategory>, count: Int) -> Bool {
        guard !categories.isEmpty else { return false }
        let only: (FileCategory) -> Bool = { categories == [$0] }
        let imageLike = categories.isSubset(of: [.image, .vector])
        switch self {
        case .compress: return categories.isSubset(of: [.image, .video, .audio, .pdf])
        case .metadata: return categories.isSubset(of: [.image, .video, .audio, .pdf])
        case .batchRename: return count > 1
        case .editImage, .annotateImage, .frameImage, .cropImage, .resizeImage, .removeBackground, .readQRCode: return imageLike
        case .redactImage: return imageLike && count == 1
        case .extractText: return categories.isSubset(of: [.image, .vector, .pdf])
        case .createCollage: return imageLike && count >= 2
        case .trimVideo, .cropVideo, .changeVideoSpeed, .muteVideo, .videoSnapshots, .videoToGIF: return only(.video) && (count == 1 || self == .muteVideo || self == .changeVideoSpeed)
        case .redactVideo, .splitVideo: return only(.video) && count == 1
        case .joinVideos: return only(.video) && count >= 2
        case .trimAudio, .redactAudio, .audioToVideo: return categories.isSubset(of: [.audio, .video]) && count == 1
        case .normalizeAudio, .audioChannels: return categories.isSubset(of: [.audio, .video])
        case .createPDF: return imageLike
        case .mergePDF: return categories.contains(.pdf) && categories.isSubset(of: [.pdf, .image]) && count >= 2
        case .splitPDF, .organizePDF: return only(.pdf) && count == 1
        case .extractArchive: return only(.archive)
        }
    }

    static func available(for urls: [URL]) -> [FileTool] {
        let cats = Set(urls.map(FileCategory.of))
        return allCases.filter { $0.applies(to: cats, count: urls.count) }
    }
}

enum FileAction: String, CaseIterable, Identifiable {
    case airDrop, float, copy, move, copyPath, openTerminal, quickLook, reveal

    var id: String { rawValue }
    var title: String {
        switch self {
        case .airDrop: return "AirDrop"
        case .float: return "Float"
        case .copy: return "Copy to…"
        case .move: return "Move to…"
        case .copyPath: return "Copy Path"
        case .openTerminal: return "Terminal"
        case .quickLook: return "Quick Look"
        case .reveal: return "Reveal"
        }
    }
    var symbol: String {
        switch self {
        case .airDrop: return "airplayaudio"
        case .float: return "paperclip"
        case .copy: return "doc.on.doc"
        case .move: return "folder"
        case .copyPath: return "link"
        case .openTerminal: return "terminal"
        case .quickLook: return "eye"
        case .reveal: return "magnifyingglass"
        }
    }
}
