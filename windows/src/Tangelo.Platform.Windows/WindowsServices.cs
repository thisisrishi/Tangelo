using System.Runtime.InteropServices.WindowsRuntime;
using ImageMagick;
using Tangelo.Core;
using Windows.Graphics.Imaging;
using Windows.Media.FaceAnalysis;
using Windows.Media.Ocr;

namespace Tangelo.Platform.Windows;

/// Registers Windows-native OCR, text-region and face detection with the cross-platform core.
public static class WindowsServices
{
    public static void Register()
    {
        Core.Platform.Faces = DetectFaces;
        if (OcrEngine.TryCreateFromUserProfileLanguages() == null && OcrEngine.AvailableRecognizerLanguages.Count == 0) return;
        Core.Platform.Ocr = async img => (await Recognize(img)).Lines.Select(l => l.Text).ToList();
        Core.Platform.TextRegions = async img =>
        {
            var (result, w, h) = await RecognizeWithSize(img);
            return result.Lines.Select(l =>
            {
                var rects = l.Words.Select(word => word.BoundingRect).ToList();
                double x0 = rects.Min(r => r.X), y0 = rects.Min(r => r.Y), x1 = rects.Max(r => r.X + r.Width), y1 = rects.Max(r => r.Y + r.Height);
                return new RectF(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h);
            }).ToList();
        };
    }

    static OcrEngine Engine => OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0])
        ?? throw new TangeloException("No OCR language is installed. Add one in Settings → Time & language → Language.");

    static async Task<OcrResult> Recognize(MagickImage img) => (await RecognizeWithSize(img)).Result;

    static async Task<(OcrResult Result, double W, double H)> RecognizeWithSize(MagickImage img)
    {
        using var bitmap = ToSoftwareBitmap(img, (int)OcrEngine.MaxImageDimension);
        var result = await Engine.RecognizeAsync(bitmap);
        return (result, bitmap.PixelWidth, bitmap.PixelHeight);
    }

    static SoftwareBitmap ToSoftwareBitmap(MagickImage source, int maxDimension, BitmapPixelFormat format = BitmapPixelFormat.Bgra8)
    {
        using var img = (MagickImage)source.Clone();
        if (Math.Max(img.Width, img.Height) > maxDimension) img.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
        img.Alpha(AlphaAction.Set);
        var bytes = img.GetPixels().ToByteArray(PixelMapping.BGRA)!;
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, (int)img.Width, (int)img.Height, BitmapAlphaMode.Premultiplied);
        if (format == BitmapPixelFormat.Bgra8) return bitmap;
        var converted = SoftwareBitmap.Convert(bitmap, format);
        bitmap.Dispose();
        return converted;
    }

    static async Task<IReadOnlyList<RectF>> DetectFaces(MagickImage img)
    {
        if (!FaceDetector.IsSupported) return [];
        var detector = await FaceDetector.CreateAsync();
        var format = FaceDetector.GetSupportedBitmapPixelFormats().Contains(BitmapPixelFormat.Gray8) ? BitmapPixelFormat.Gray8 : FaceDetector.GetSupportedBitmapPixelFormats()[0];
        using var bitmap = ToSoftwareBitmap(img, 2048, format);
        var faces = await detector.DetectFacesAsync(bitmap);
        double w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        // Pad boxes a little so hair and chin are covered too.
        return faces.Select(f => new RectF(
            Math.Max(0, (f.FaceBox.X - f.FaceBox.Width * 0.15) / w), Math.Max(0, (f.FaceBox.Y - f.FaceBox.Height * 0.2) / h),
            Math.Min(1, f.FaceBox.Width * 1.3 / w), Math.Min(1, f.FaceBox.Height * 1.4 / h))).ToList();
    }
}
