using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tangelo.Core;

public enum OutputLocation { SameFolder, Downloads, Desktop, Custom }

/// User settings persisted as JSON in %APPDATA%\Tangelo\settings.json.
public sealed class Settings
{
    public OutputLocation OutputLocation { get; set; } = OutputLocation.SameFolder;
    public string CustomOutputPath { get; set; } = "";
    public bool RevealOutputs { get; set; } = true;
    public bool SoundFeedback { get; set; } = true;
    public bool LaunchAtLogin { get; set; } = true;
    public bool ShakeToFloat { get; set; } = true;
    public bool RestoreFloats { get; set; } = true;
    public bool RemoveAfterDragOut { get; set; }
    public bool ExplorerMenu { get; set; } = true;
    public string Terminal { get; set; } = "Windows Terminal";
    public string FFmpegPath { get; set; } = "";

    // Wheel shortcuts: modifier names joined by '+', e.g. "Shift", "Ctrl+Alt".
    public string ConversionsModifier { get; set; } = "Shift";
    public string ToolsModifier { get; set; } = "Ctrl";
    public string ActionsModifier { get; set; } = "Alt";
    public bool ConversionsWheelEnabled { get; set; } = true;
    public bool ToolsWheelEnabled { get; set; } = true;
    public bool ActionsWheelEnabled { get; set; } = true;

    // Conversion defaults
    public int ImageQuality { get; set; } = 85;
    public bool PreserveMetadata { get; set; } = true;
    public int AudioBitrate { get; set; } = 192;
    public bool PreferHevc { get; set; }
    public int GifFps { get; set; } = 12;
    public int GifWidth { get; set; } = 640;

    public bool DidOnboard { get; set; }
    public List<FloatState> Floats { get; set; } = [];

    public sealed class FloatState
    {
        public List<string> Paths { get; set; } = [];
        public double Left { get; set; }
        public double Top { get; set; }
    }

    public static string Directory
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tangelo");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    static Settings? current;
    public static Settings Current => current ??= Load();

    /// True when no settings file existed yet (first launch after install).
    [JsonIgnore] public bool IsNew { get; private set; }

    static Settings Load()
    {
        try
        {
            var path = Path.Combine(Directory, "settings.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json) ?? new();
        }
        catch { }
        return new() { IsNew = true };
    }

    public void Save()
    {
        try { File.WriteAllText(Path.Combine(Directory, "settings.json"), JsonSerializer.Serialize(this, Json)); } catch { }
    }

    /// For tests: use an isolated settings object.
    public static void Override(Settings s) => current = s;
}

public static class Output
{
    public static string DirectoryFor(string source)
    {
        var s = Settings.Current;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        switch (s.OutputLocation)
        {
            case OutputLocation.Downloads: return Path.Combine(home, "Downloads");
            case OutputLocation.Desktop: return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            case OutputLocation.Custom when System.IO.Directory.Exists(s.CustomOutputPath): return s.CustomOutputPath;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(source))!;
        return IsWritable(dir) ? dir : Path.Combine(home, "Downloads");
    }

    static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".tangelo-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// "photo.heic" + "png" → "photo.png", or "photo 2.png" when taken.
    public static string PathFor(string source, string ext, string? suffix = null, string? dir = null)
    {
        var name = Path.GetFileNameWithoutExtension(source.TrimEnd(Path.DirectorySeparatorChar));
        if (suffix != null) name += " " + suffix;
        return Unique(dir ?? DirectoryFor(source), name, ext);
    }

    public static string Unique(string dir, string baseName, string ext)
    {
        string Make(string n) => Path.Combine(dir, ext.Length == 0 ? n : $"{n}.{ext}");
        var candidate = Make(baseName);
        for (int i = 2; File.Exists(candidate) || System.IO.Directory.Exists(candidate); i++) candidate = Make($"{baseName} {i}");
        return candidate;
    }

    public static string NewFolder(string source, string suffix)
    {
        var dir = Unique(DirectoryFor(source), Path.GetFileNameWithoutExtension(source) + " " + suffix, "");
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static string TempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Tangelo-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    public static long SizeOf(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (System.IO.Directory.Exists(path)) return new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        return 0;
    }

    public static string FormatBytes(long n)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB"];
        double v = n; int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{n} bytes" : $"{v:0.#} {units[u]}";
    }
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Date { get; set; } = DateTime.Now;
    public string Title { get; set; } = "";
    public List<string> Inputs { get; set; } = [];
    public List<string> Outputs { get; set; } = [];
    public string? Error { get; set; }
}

/// Every operation is logged so outputs can be revealed or undone (sent to the Recycle Bin).
public static class History
{
    static readonly object Gate = new();
    static List<HistoryEntry>? entries;
    public static event Action? Changed;

    static string FilePath => Path.Combine(Settings.Directory, "history.json");

    public static IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (Gate)
            {
                if (entries == null)
                {
                    try { entries = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) : null; } catch { }
                    entries ??= [];
                }
                return entries.ToList();
            }
        }
    }

    public static void Add(HistoryEntry e)
    {
        lock (Gate)
        {
            _ = Entries;
            entries!.Insert(0, e);
            if (entries.Count > 200) entries.RemoveRange(200, entries.Count - 200);
            Save();
        }
        Changed?.Invoke();
    }

    public static void Remove(Guid id)
    {
        lock (Gate) { _ = Entries; entries!.RemoveAll(e => e.Id == id); Save(); }
        Changed?.Invoke();
    }

    public static void Clear()
    {
        lock (Gate) { entries = []; Save(); }
        Changed?.Invoke();
    }

    static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(entries)); } catch { }
    }
}

public class TangeloException(string message) : Exception(message);

public sealed class ToolMissingException(string tool, string hint)
    : TangeloException($"{tool} is required for this operation. {hint}");

/// Locates optional external helpers.
public static class Helpers
{
    static string? Find(string name, string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[] { Path.Combine(baseDir, "tools", exe), Path.Combine(baseDir, exe) })
            if (File.Exists(candidate)) return candidate;
        var extra = OperatingSystem.IsWindows() ? Array.Empty<string>() : ["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(extra))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var p = Path.Combine(dir.Trim(), exe);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    public static string? FFmpeg => Find("ffmpeg", Settings.Current.FFmpegPath);
    public static string? FFprobe
    {
        get
        {
            var ff = FFmpeg;
            if (ff != null)
            {
                var sibling = Path.Combine(Path.GetDirectoryName(ff)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                if (File.Exists(sibling)) return sibling;
            }
            return Find("ffprobe");
        }
    }

    public static string? LibreOffice
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                {
                    var p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                    if (File.Exists(p)) return p;
                }
            }
            else if (File.Exists("/Applications/LibreOffice.app/Contents/MacOS/soffice")) return "/Applications/LibreOffice.app/Contents/MacOS/soffice";
            return Find("soffice");
        }
    }

    public static string RequireFFmpeg() => FFmpeg ?? throw new ToolMissingException("ffmpeg",
        OperatingSystem.IsWindows() ? "Reinstall Tangelo (it bundles ffmpeg) or run: winget install Gyan.FFmpeg" : "Run: brew install ffmpeg");
}

public static class Shell
{
    public sealed record Result(int Status, byte[] Stdout, string Stderr)
    {
        public string Text => Encoding.UTF8.GetString(Stdout);
    }

    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, string? cwd = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (cwd != null) psi.WorkingDirectory = cwd;
        using var p = Process.Start(psi) ?? throw new TangeloException($"Could not start {Path.GetFileName(exe)}.");
        using var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms, ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(outTask, errTask, p.WaitForExitAsync(ct));
        return new Result(p.ExitCode, ms.ToArray(), errTask.Result);
    }

    public static async Task<Result> CheckAsync(string exe, IEnumerable<string> args, string? cwd = null, CancellationToken ct = default)
    {
        var r = await RunAsync(exe, args, cwd, ct);
        if (r.Status != 0)
        {
            var tail = string.Join("\n", r.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
            throw new TangeloException($"{Path.GetFileNameWithoutExtension(exe)} failed: {tail}");
        }
        return r;
    }
}
