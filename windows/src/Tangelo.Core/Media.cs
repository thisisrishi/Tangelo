using System.Globalization;
using System.Text.Json;

namespace Tangelo.Core;

public sealed record MediaInfo(double Duration, int Width, int Height, bool HasVideo, bool HasAudio, double FrameRate, long VideoBitrate, int AudioChannels);

public sealed record RectF(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// A normalized (0–1, top-left origin) rectangle, optionally limited to a time range.
public sealed record Redaction(RectF Rect, double? Start = null, double? End = null);

public enum RedactionStyle { Blur, Pixelate, Solid }

public enum VisualizerStyle { Bars, Wave, Circle, Mirror }

/// All audio and video work runs through ffmpeg so behaviour is identical on every machine.
public static class Media
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double v) => v.ToString("0.###", Inv);

    public static async Task<MediaInfo> InfoAsync(string path)
    {
        var probe = Helpers.FFprobe ?? throw new ToolMissingException("ffprobe", "It ships alongside ffmpeg.");
        var r = await Shell.CheckAsync(probe, ["-v", "error", "-print_format", "json", "-show_streams", "-show_format", path]);
        using var doc = JsonDocument.Parse(r.Text);
        var root = doc.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d)) double.TryParse(d.GetString(), NumberStyles.Float, Inv, out duration);
        int w = 0, h = 0, channels = 0; bool hasV = false, hasA = false; double fps = 30; long br = 0;
        foreach (var s in root.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            if (type == "video" && !hasV && !(s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1))
            {
                hasV = true;
                w = s.GetProperty("width").GetInt32(); h = s.GetProperty("height").GetInt32();
                if (s.TryGetProperty("r_frame_rate", out var rf) && rf.GetString() is { } rate && rate.Split('/') is [var n, var dd]
                    && double.TryParse(n, Inv, out var nn) && double.TryParse(dd, Inv, out var dn) && dn > 0) fps = nn / dn;
                if (s.TryGetProperty("bit_rate", out var b)) long.TryParse(b.GetString(), out br);
                // Honour rotation metadata so sizes match what players show.
                if (s.TryGetProperty("side_data_list", out var sdl))
                    foreach (var sd in sdl.EnumerateArray())
                        if (sd.TryGetProperty("rotation", out var rot) && Math.Abs(rot.GetDouble()) % 180 == 90) (w, h) = (h, w);
            }
            if (type == "audio" && !hasA) { hasA = true; if (s.TryGetProperty("channels", out var c)) channels = c.GetInt32(); }
        }
        return new MediaInfo(duration, w, h, hasV, hasA, fps, br, channels);
    }

    static async Task Run(IEnumerable<string> args, CancellationToken ct = default)
    {
        await Shell.CheckAsync(Helpers.RequireFFmpeg(), ["-y", "-hide_banner", "-v", "error", .. args], ct: ct);
    }

    static HashSet<string>? encoders;

    /// Encoders compiled into the installed ffmpeg (builds differ between distributors).
    static HashSet<string> Encoders
    {
        get
        {
            if (encoders != null) return encoders;
            var set = new HashSet<string>();
            try
            {
                var r = Shell.RunAsync(Helpers.RequireFFmpeg(), ["-hide_banner", "-encoders"]).GetAwaiter().GetResult();
                foreach (var line in r.Text.Split('\n'))
                {
                    var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
                }
            }
            catch (ToolMissingException) { }
            return encoders = set;
        }
    }

    static string[] Pick(string preferred, string fallback, params string[] fallbackExtra) =>
        Encoders.Contains(preferred) || !Encoders.Contains(fallback) ? ["-c:a", preferred] : ["-c:a", fallback, .. fallbackExtra];

    public static string[] AudioArgs(ConversionFormat f, int kbps) => f switch
    {
        ConversionFormat.Mp3 => ["-c:a", "libmp3lame", "-b:a", $"{kbps}k"],
        ConversionFormat.M4a => ["-c:a", "aac", "-b:a", $"{kbps}k"],
        ConversionFormat.Ogg => [.. Pick("libvorbis", "vorbis", "-strict", "-2", "-ac", "2"), "-b:a", $"{kbps}k"],
        ConversionFormat.Opus => [.. Pick("libopus", "opus", "-strict", "-2", "-ar", "48000"), "-b:a", $"{Math.Min(kbps, 256)}k"],
        ConversionFormat.Wma => ["-c:a", "wmav2", "-b:a", $"{kbps}k"],
        ConversionFormat.Flac => ["-c:a", "flac"],
        ConversionFormat.Wav => ["-c:a", "pcm_s16le"],
        ConversionFormat.Aiff => ["-c:a", "pcm_s16be"],
        _ => [],
    };

    public static string[] VideoArgs(ConversionFormat f, bool hevc = false) => f switch
    {
        ConversionFormat.Webm => ["-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus"],
        ConversionFormat.Avi => ["-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame"],
        ConversionFormat.Wmv => ["-c:v", "wmv2", "-b:v", "4M", "-c:a", "wmav2"],
        ConversionFormat.Mkv => ["-c:v", hevc ? "libx265" : "libx264", "-crf", "20", "-preset", "medium", "-c:a", "aac"],
        _ => ["-c:v", hevc ? "libx265" : "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p", .. (hevc ? new[] { "-tag:v", "hvc1" } : []), "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"],
    };

    static string[] H264 => ["-c:v", "libx264", "-crf", "20", "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"];

    public static ConversionFormat VideoOutputFormat(string path) =>
        Formats.From(path) is ConversionFormat.Mov or ConversionFormat.M4v or ConversionFormat.Mp4 or ConversionFormat.Mkv ? Formats.From(path)!.Value : ConversionFormat.Mp4;

    public static ConversionFormat AudioOutputFormat(string path) =>
        Formats.From(path) is { } f && f.Category() == FileCategory.Audio ? f : ConversionFormat.M4a;

    // MARK: Conversions

    public static async Task ConvertVideo(string input, string output, ConversionFormat f) =>
        await Run(["-i", input, .. VideoArgs(f, Settings.Current.PreferHevc), output]);

    public static async Task ConvertAudio(string input, string output, ConversionFormat f) =>
        await Run(["-i", input, "-vn", .. AudioArgs(f, Settings.Current.AudioBitrate), output]);

    public static async Task Gif(string input, string output, int fps, int width, double? start = null, double? end = null)
    {
        var range = new List<string>();
        if (start is { } s) range.AddRange(["-ss", F(s)]);
        if (end is { } e) range.AddRange(["-t", F(e - (start ?? 0))]);
        else range.AddRange(["-t", "30"]);
        await Run([.. range, "-i", input, "-vf",
            $"fps={fps},scale='min({width},iw)':-2:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4", "-loop", "0", output]);
    }

    // MARK: Video tools

    public static async Task<string> Trim(string input, double start, double end)
    {
        var f = FormatsFor(input);
        var output = Output.PathFor(input, f.Extension(), "trimmed");
        // Re-encode for frame accuracy; stream copy snaps to keyframes.
        if (Formats.CategoryOf(input) == FileCategory.Audio)
            await Run(["-ss", F(start), "-to", F(end), "-i", input, .. AudioArgs(f, Settings.Current.AudioBitrate), output]);
        else
            await Run(["-ss", F(start), "-to", F(end), "-i", input, .. H264, output]);
        return output;
    }

    static ConversionFormat FormatsFor(string input) =>
        Formats.CategoryOf(input) == FileCategory.Audio ? AudioOutputFormat(input) : VideoOutputFormat(input);

    public static async Task<string> Crop(string input, RectF r)
    {
        var info = await InfoAsync(input);
        int w = Even(r.W * info.Width), h = Even(r.H * info.Height), x = (int)(r.X * info.Width), y = (int)(r.Y * info.Height);
        if (w < 2 || h < 2) throw new TangeloException("Make the crop at least 2 pixels wide.");
        var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), "cropped");
        await Run(["-i", input, "-vf", $"crop={w}:{h}:{x}:{y}", .. H264, output]);
        return output;
    }

    static int Even(double v) => Math.Max(2, (int)(v / 2) * 2);

    public static async Task<string> ChangeSpeed(string input, double factor)
    {
        if (factor is < 0.25 or > 4) throw new TangeloException("Choose a speed between 0.25× and 4×.");
        var info = await InfoAsync(input);
        var label = factor == Math.Floor(factor) ? $"{factor:0}x" : $"{factor.ToString("0.##", Inv)}x";
        var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), label);
        var args = new List<string> { "-i", input, "-filter:v", $"setpts={F(1 / factor)}*PTS" };
        if (info.HasAudio) args.AddRange(["-filter:a", AtempoChain(factor)]); else args.Add("-an");
        await Run([.. args, .. H264, output]);
        return output;
    }

    /// atempo accepts 0.5–2 per stage; chain stages for larger factors.
    static string AtempoChain(double f)
    {
        var parts = new List<string>();
        while (f > 2) { parts.Add("atempo=2"); f /= 2; }
        while (f < 0.5) { parts.Add("atempo=0.5"); f /= 0.5; }
        parts.Add($"atempo={F(f)}");
        return string.Join(",", parts);
    }

    public static async Task<string> Mute(string input)
    {
        var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), "muted");
        await Run(["-i", input, "-c:v", "copy", "-an", output]);
        return output;
    }

    /// Joins segments (file, optional range) into one video sized to the first, letterboxing others.
    public static async Task Join(IReadOnlyList<(string Path, double? Start, double? End)> segments, string output)
    {
        var first = await InfoAsync(segments[0].Path);
        int W = Even(first.Width), H = Even(first.Height);
        var args = new List<string>();
        var filters = new List<string>();
        var allAudio = true;
        foreach (var s in segments) allAudio &= (await InfoAsync(s.Path)).HasAudio;
        for (int i = 0; i < segments.Count; i++)
        {
            var (p, a, b) = segments[i];
            if (a is { } st) args.AddRange(["-ss", F(st)]);
            if (b is { } en) args.AddRange(["-to", F(en)]);
            args.AddRange(["-i", p]);
            filters.Add($"[{i}:v]scale={W}:{H}:force_original_aspect_ratio=decrease,pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}]");
            if (allAudio) filters.Add($"[{i}:a]aformat=sample_rates=48000:channel_layouts=stereo[a{i}]");
        }
        var inputs = string.Concat(Enumerable.Range(0, segments.Count).Select(i => allAudio ? $"[v{i}][a{i}]" : $"[v{i}]"));
        filters.Add($"{inputs}concat=n={segments.Count}:v=1:a={(allAudio ? 1 : 0)}[v]" + (allAudio ? "[a]" : ""));
        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[v]"]);
        if (allAudio) args.AddRange(["-map", "[a]"]);
        await Run([.. args, .. H264, output]);
    }

    public static async Task<string> JoinVideos(IEnumerable<string> inputs)
    {
        var sorted = inputs.OrderBy(p => Path.GetFileName(p), NaturalComparer.Instance).ToList();
        var output = Output.PathFor(sorted[0], "mp4", "joined");
        await Join(sorted.Select(p => (p, (double?)null, (double?)null)).ToList(), output);
        return output;
    }

    public static async Task<List<string>> Split(string input, IEnumerable<double> points, ISet<int> removed, bool joinKept)
    {
        var info = await InfoAsync(input);
        var bounds = new List<double> { 0 };
        bounds.AddRange(points.Order());
        bounds.Add(info.Duration);
        var clips = new List<(double, double)>();
        for (int i = 0; i < bounds.Count - 1; i++) if (bounds[i + 1] - bounds[i] >= 0.05) clips.Add((bounds[i], bounds[i + 1]));
        var kept = clips.Select((c, i) => (c, i)).Where(x => !removed.Contains(x.i)).ToList();
        if (kept.Count == 0) throw new TangeloException("Keep at least one clip.");
        var ext = VideoOutputFormat(input).Extension();
        if (joinKept)
        {
            var output = Output.PathFor(input, ext, "edited");
            await Join(kept.Select(k => (input, (double?)k.c.Item1, (double?)k.c.Item2)).ToList(), output);
            return [output];
        }
        var outs = new List<string>();
        foreach (var (c, i) in kept)
        {
            var output = Output.PathFor(input, ext, $"clip {i + 1}");
            await Run(["-ss", F(c.Item1), "-to", F(c.Item2), "-i", input, .. H264, output]);
            outs.Add(output);
        }
        return outs;
    }

    public static async Task<List<string>> Snapshots(string input, IReadOnlyList<double> times)
    {
        if (times.Count == 0) throw new TangeloException("Select at least one frame within the video.");
        var dir = times.Count > 1 ? Output.NewFolder(input, "frames") : Output.DirectoryFor(input);
        var outs = new List<string>();
        foreach (var t in times)
        {
            var output = Output.Unique(dir, Path.GetFileNameWithoutExtension(input) + $" {t.ToString("0.000", Inv)}s", "png");
            await Frame(input, t, output);
            outs.Add(output);
        }
        return outs;
    }

    /// Writes one frame to a PNG (also used for editor previews).
    public static async Task Frame(string input, double t, string output, int? maxWidth = null)
    {
        var vf = maxWidth is { } mw ? new[] { "-vf", $"scale='min({mw},iw)':-2" } : [];
        await Run(["-ss", F(t), "-i", input, "-frames:v", "1", .. vf, output]);
    }

    public static async Task<string> Redact(string input, IReadOnlyList<Redaction> rects, RedactionStyle style)
    {
        if (rects.Count == 0) throw new TangeloException("Draw a rectangle over the area to hide.");
        var info = await InfoAsync(input);
        var filters = new List<string>();
        var last = "0:v";
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            int w = Even(r.Rect.W * info.Width), h = Even(r.Rect.H * info.Height), x = (int)(r.Rect.X * info.Width), y = (int)(r.Rect.Y * info.Height);
            var enable = r.Start is { } s && r.End is { } e ? $":enable='between(t,{F(s)},{F(e)})'" : "";
            if (style == RedactionStyle.Solid)
            {
                filters.Add($"[{last}]drawbox=x={x}:y={y}:w={w}:h={h}:color=black:t=fill{enable}[r{i}]");
            }
            else
            {
                var effect = style == RedactionStyle.Blur
                    ? $"boxblur={Math.Max(4, Math.Min(w, h) / 8)}:2"
                    : $"scale=max(2\\,iw/{Math.Max(4, Math.Max(w, h) / 12)}):-2:flags=neighbor,scale={w}:{h}:flags=neighbor";
                filters.Add($"[{last}]split[b{i}][s{i}];[s{i}]crop={w}:{h}:{x}:{y},{effect}[p{i}];[b{i}][p{i}]overlay={x}:{y}{enable}[r{i}]");
            }
            last = $"r{i}";
        }
        var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), "redacted");
        await Run(["-i", input, "-filter_complex", string.Join(";", filters), "-map", $"[{last}]", "-map", "0:a?", .. H264, output]);
        return output;
    }

    public enum VideoPreset { High, Balanced, Small, Smallest }

    public static async Task<string> CompressVideo(string input, VideoPreset? preset, long? targetBytes)
    {
        var info = await InfoAsync(input);
        int maxH; long vbr; int abr = 128;
        bool hevc = false;
        if (targetBytes is { } t)
        {
            var audioBits = info.HasAudio ? 96_000.0 : 0;
            var videoBits = t * 8 * 0.93 / Math.Max(info.Duration, 0.1) - audioBits;
            if (videoBits < 80_000) throw new TangeloException("The target is too small for this duration. Choose a larger size.");
            vbr = (long)videoBits; abr = 96;
            maxH = videoBits < 1_000_000 ? 540 : videoBits < 2_500_000 ? 720 : 1080;
        }
        else
        {
            (maxH, vbr, abr, hevc) = preset switch
            {
                VideoPreset.High => (1080, 5_000_000L, 160, true),
                VideoPreset.Small => (720, 2_000_000L, 96, false),
                VideoPreset.Smallest => (540, 900_000L, 64, false),
                _ => (1080, 4_000_000L, 128, false),
            };
        }
        if (info.VideoBitrate > 0 && vbr > info.VideoBitrate) vbr = (long)(info.VideoBitrate * 0.8);
        var output = Output.PathFor(input, "mp4", "compressed");
        var codec = hevc ? new[] { "-c:v", "libx265", "-tag:v", "hvc1" } : ["-c:v", "libx264"];
        await Run(["-i", input, "-vf", $"scale=-2:'min({maxH},ih)'", .. codec, "-b:v", vbr.ToString(Inv), "-maxrate", ((long)(vbr * 1.2)).ToString(Inv),
            "-bufsize", (vbr * 2).ToString(Inv), "-preset", "medium", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", $"{abr}k", "-movflags", "+faststart", output]);
        return output;
    }

    // MARK: Audio tools (work on audio files and on the audio track of videos)

    static async Task<string> ProcessAudio(string input, string filter, string suffix)
    {
        if (Formats.CategoryOf(input) == FileCategory.Video)
        {
            var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), suffix);
            await Run(["-i", input, "-c:v", "copy", "-af", filter, "-c:a", "aac", "-b:a", "192k", output]);
            return output;
        }
        var f = AudioOutputFormat(input);
        var path = Output.PathFor(input, f.Extension(), suffix);
        await Run(["-i", input, "-vn", "-af", filter, .. AudioArgs(f, Settings.Current.AudioBitrate), path]);
        return path;
    }

    public static Task<string> Normalize(string input, double targetLufs, double truePeak) =>
        ProcessAudio(input, $"loudnorm=I={F(targetLufs)}:TP={F(truePeak)}:LRA=11", "normalized");

    public static Task<string> Channels(string input, int count, double left, double right) => count == 1
        ? ProcessAudio(input, $"pan=mono|c0={F(left / 2)}*c0+{F(right / 2)}*c1", "mono")
        : ProcessAudio(input, $"pan=stereo|c0={F(left)}*c0|c1={F(right)}*c1", "stereo");

    public static async Task<string> Bleep(string input, IReadOnlyList<(double Start, double End)> ranges, bool tone)
    {
        if (ranges.Count == 0) throw new TangeloException("Add at least one bleep range.");
        var enable = string.Join("+", ranges.Select(r => $"between(t,{F(r.Start)},{F(r.End)})"));
        // Silence the ranges, then (optionally) mix in a 1 kHz tone gated to the same ranges.
        var graph = tone
            ? $"[0:a]volume=enable='{enable}':volume=0[m];sine=f=1000:r=48000,volume=0.3,volume=enable='not({enable})':volume=0[t];[m][t]amix=inputs=2:duration=first:normalize=0[a]"
            : $"[0:a]volume=enable='{enable}':volume=0[a]";
        if (Formats.CategoryOf(input) == FileCategory.Video)
        {
            var output = Output.PathFor(input, VideoOutputFormat(input).Extension(), "bleeped");
            await Run(["-i", input, "-filter_complex", graph, "-map", "0:v", "-map", "[a]", "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", output]);
            return output;
        }
        var f = AudioOutputFormat(input);
        var path = Output.PathFor(input, f.Extension(), "bleeped");
        await Run(["-i", input, "-filter_complex", graph, "-map", "[a]", .. AudioArgs(f, Settings.Current.AudioBitrate), path]);
        return path;
    }

    public static async Task<string> TrimAudio(string input, double start, double end, bool trimSilence, double thresholdDb)
    {
        if (Formats.CategoryOf(input) == FileCategory.Video) return await Trim(input, start, end);
        var f = AudioOutputFormat(input);
        var output = Output.PathFor(input, f.Extension(), "trimmed");
        var af = trimSilence
            ? new[] { "-af", $"silenceremove=start_periods=1:start_threshold={F(thresholdDb)}dB,areverse,silenceremove=start_periods=1:start_threshold={F(thresholdDb)}dB,areverse" }
            : [];
        await Run(["-ss", F(start), "-to", F(end), "-i", input, "-vn", .. af, .. AudioArgs(f, Settings.Current.AudioBitrate), output]);
        return output;
    }

    public enum AudioPreset { High, Balanced, Small, Voice }

    public static async Task<string> CompressAudio(string input, AudioPreset? preset, long? targetBytes)
    {
        var info = await InfoAsync(input);
        int kbps = preset switch { AudioPreset.High => 192, AudioPreset.Small => 96, AudioPreset.Voice => 64, _ => 128 };
        if (targetBytes is { } t) kbps = (int)Math.Clamp(t * 8 * 0.97 / Math.Max(info.Duration, 0.1) / 1000, 32, 320);
        var f = Formats.From(input) == ConversionFormat.Mp3 ? ConversionFormat.Mp3 : ConversionFormat.M4a;
        var output = Output.PathFor(input, f.Extension(), "compressed");
        var mono = preset == AudioPreset.Voice || kbps < 64 ? new[] { "-ac", "1" } : [];
        await Run(["-i", input, "-vn", .. mono, .. AudioArgs(f, kbps), output]);
        return output;
    }

    /// Peak envelope of the first audio track (for waveform drawing).
    public static async Task<float[]> Envelope(string input, int buckets)
    {
        var r = await Shell.CheckAsync(Helpers.RequireFFmpeg(), ["-v", "error", "-i", input, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", "-"]);
        var samples = r.Stdout.Length / 2;
        if (samples == 0) return [];
        var env = new float[buckets];
        int step = Math.Max(1, samples / buckets);
        for (int b = 0; b < buckets; b++)
        {
            int start = b * step, end = Math.Min(samples, start + step);
            float m = 0;
            for (int i = start; i < end; i++) m = Math.Max(m, Math.Abs(BitConverter.ToInt16(r.Stdout, i * 2) / 32768f));
            env[b] = m;
        }
        var max = env.Max();
        return max > 0 ? env.Select(v => v / max).ToArray() : env;
    }

    public sealed record VisualizerOptions(VisualizerStyle Style = VisualizerStyle.Bars, string Accent = "#FF5200", string Background = "#141414",
                                           string? ImagePath = null, int Width = 1280, int Height = 720, string Title = "");

    public static async Task<string> Visualize(string input, VisualizerOptions o)
    {
        var output = Output.PathFor(input, "mp4", "visualizer");
        int W = o.Width, H = o.Height;
        // Visual filters render white; tint to the accent colour (works with every ffmpeg build).
        var hex = o.Accent.TrimStart('#');
        double R = System.Convert.ToInt32(hex[..2], 16) / 255.0, G = System.Convert.ToInt32(hex[2..4], 16) / 255.0, B = System.Convert.ToInt32(hex[4..6], 16) / 255.0;
        var tint = $",format=rgba,colorchannelmixer=rr={F(R)}:rg=0:rb=0:gr=0:gg={F(G)}:gb=0:br=0:bg=0:bb={F(B)}";
        var wave = o.Style switch
        {
            VisualizerStyle.Wave => $"showwaves=s={W}x{H / 2}:mode=cline:rate=30:colors=white:scale=sqrt{tint}",
            VisualizerStyle.Circle => $"avectorscope=s={H / 2}x{H / 2}:rate=30:rc=255:gc=255:bc=255:zoom=1.5:draw=line{tint}",
            VisualizerStyle.Mirror => $"showwaves=s={W}x{H / 2}:mode=p2p:rate=30:colors=white{tint}",
            _ => $"showfreqs=s={W}x{H / 2}:mode=bar:ascale=sqrt:fscale=log:win_size=2048,fps=30{tint}",
        };
        // Background and title are drawn with Magick.NET so no ffmpeg font support is needed.
        var bgPath = Path.Combine(Output.TempDirectory(), "bg.png");
        using (var bgImg = o.ImagePath is { } img && File.Exists(img) ? Images.Load(img) : new ImageMagick.MagickImage(new ImageMagick.MagickColor(o.Background), (uint)W, (uint)H))
        {
            bgImg.Resize(new ImageMagick.MagickGeometry((uint)W, (uint)H) { FillArea = true });
            bgImg.Extent((uint)W, (uint)H, ImageMagick.Gravity.Center);
            if (o.ImagePath != null) bgImg.Evaluate(ImageMagick.Channels.RGB, ImageMagick.EvaluateOperator.Multiply, 0.6);
            if (!string.IsNullOrWhiteSpace(o.Title))
                new ImageMagick.Drawing.Drawables().FontPointSize(H / 18.0).FillColor(ImageMagick.MagickColors.White)
                    .Text(W * 0.06, H * 0.08 + H / 18.0, o.Title).Draw(bgImg);
            bgImg.Write(bgPath, ImageMagick.MagickFormat.Png);
        }
        // Bound the looped background to the audio length so filters never buffer an endless stream.
        var duration = (await InfoAsync(input)).Duration;
        var args = new List<string> { "-loop", "1", "-framerate", "30", "-t", F(Math.Max(duration, 0.1)), "-i", bgPath, "-i", input };
        var graph = $"[0:v]setsar=1,format=yuv420p[bg];[1:a]{wave}[w];[bg][w]overlay=(W-w)/2:(H-h)/2:shortest=1,format=yuv420p[v]";
        await Run([.. args, "-filter_complex", graph, "-map", "[v]", "-map", "1:a", "-c:v", "libx264", "-preset", "medium", "-crf", "22",
            "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output]);
        return output;
    }

    public static async Task<string> StripMetadata(string input)
    {
        var cat = Formats.CategoryOf(input);
        var ext = cat == FileCategory.Audio ? Path.GetExtension(input).TrimStart('.') : VideoOutputFormat(input).Extension();
        var output = Output.PathFor(input, ext, "clean");
        await Run(["-i", input, "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy", output]);
        return output;
    }

    public static async Task<List<(string Key, string Value)>> Metadata(string input)
    {
        var probe = Helpers.FFprobe ?? throw new ToolMissingException("ffprobe", "It ships alongside ffmpeg.");
        var r = await Shell.CheckAsync(probe, ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", input]);
        using var doc = JsonDocument.Parse(r.Text);
        var list = new List<(string, string)>();
        if (doc.RootElement.TryGetProperty("format", out var fmt))
        {
            foreach (var k in new[] { "format_long_name", "duration", "bit_rate", "size" })
                if (fmt.TryGetProperty(k, out var v)) list.Add((k, v.ToString()));
            if (fmt.TryGetProperty("tags", out var tags)) foreach (var t in tags.EnumerateObject()) list.Add((t.Name, t.Value.ToString()));
        }
        foreach (var s in doc.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = s.GetProperty("codec_type").GetString();
            var codec = s.TryGetProperty("codec_long_name", out var c) ? c.GetString() : "";
            var extra = type == "video" && s.TryGetProperty("width", out var w) ? $" {w}×{s.GetProperty("height")}" : "";
            list.Add(($"{type} stream", codec + extra));
        }
        return list;
    }
}

/// Orders "clip 2" before "clip 10", like Explorer.
public sealed class NaturalComparer : IComparer<string?>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? a, string? b)
    {
        if (a == null || b == null) return string.Compare(a, b, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[si..i].TrimStart('0'); var nb = b[sj..j].TrimStart('0');
                int c = na.Length != nb.Length ? na.Length.CompareTo(nb.Length) : string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}
