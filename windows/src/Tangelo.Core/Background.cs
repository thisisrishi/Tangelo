using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tangelo.Core;

/// Subject lift using the U²-Netp salient-object model (Apache-2.0), run locally with ONNX Runtime.
public static class Background
{
    const int Size = 320;
    static InferenceSession? session;

    public static string? ModelPath
    {
        get
        {
            foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "models"), AppContext.BaseDirectory })
            {
                var p = Path.Combine(dir, "u2netp.onnx");
                if (File.Exists(p)) return p;
            }
            return null;
        }
    }

    public static bool Available => ModelPath != null;

    public static MagickImage RemoveBackground(MagickImage source)
    {
        var model = ModelPath ?? throw new TangeloException("The background-removal model is missing. Reinstall Tangelo.");
        session ??= new InferenceSession(model);

        using var small = (MagickImage)source.Clone();
        small.Alpha(AlphaAction.Remove);
        small.Resize(new MagickGeometry(Size, Size) { IgnoreAspectRatio = true });
        var rgb = small.GetPixels().ToByteArray(PixelMapping.RGB)!;

        float[] mean = [0.485f, 0.456f, 0.406f], std = [0.229f, 0.224f, 0.225f];
        var input = new DenseTensor<float>([1, 3, Size, Size]);
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                for (int c = 0; c < 3; c++)
                    input[0, c, y, x] = (rgb[(y * Size + x) * 3 + c] / 255f - mean[c]) / std[c];

        var name = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var output = results.First().AsTensor<float>();
        float min = float.MaxValue, max = float.MinValue;
        foreach (var v in output) { min = Math.Min(min, v); max = Math.Max(max, v); }
        var maskBytes = new byte[Size * Size];
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
                maskBytes[y * Size + x] = (byte)Math.Clamp((output[0, 0, y, x] - min) / Math.Max(max - min, 1e-6f) * 255, 0, 255);

        var settings = new MagickReadSettings { Width = Size, Height = Size, Format = MagickFormat.Gray, Depth = 8 };
        using var mask = new MagickImage(maskBytes, settings);
        mask.Resize(new MagickGeometry(source.Width, source.Height) { IgnoreAspectRatio = true });
        var result = (MagickImage)source.Clone();
        result.Alpha(AlphaAction.Set);
        result.Composite(mask, CompositeOperator.CopyAlpha);
        result.Trim();
        result.ResetPage();
        return result;
    }

    public static string RemoveBackground(string path)
    {
        using var img = Images.Load(path);
        using var result = RemoveBackground(img);
        var output = Output.PathFor(path, "png", "no background");
        Images.Save(result, output, ConversionFormat.Png);
        return output;
    }
}
