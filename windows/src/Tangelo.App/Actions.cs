using System.Diagnostics;
using System.Windows;
using Tangelo.App.Editors;
using Tangelo.Core;

namespace Tangelo.App;

/// Single entry point for everything the wheels, floats, tray and Explorer menu can do.
static class Actions
{
    public static void Convert(IReadOnlyList<string> paths, ConversionFormat f)
    {
        if (paths.Count == 0) return;
        var title = paths.Count == 1 ? $"{Path.GetFileName(paths[0])} → {f.Label()}" : $"{paths.Count} files → {f.Label()}";
        Jobs.Run(title, paths, async progress => await Converter.Convert(paths, f, progress));
    }

    public static void RunTool(FileTool tool, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (!tool.RunsImmediately()) { EditorWindow.Open(tool, paths); return; }
        switch (tool)
        {
            case FileTool.MuteVideo: Jobs.Batch(tool, paths, async p => (IEnumerable<string>)[await Media.Mute(p)]); break;
            case FileTool.ExtractArchive: Jobs.Batch(tool, paths, Archives.Extract); break;
            case FileTool.RemoveBackground: Jobs.Batch(tool, paths, Background.RemoveBackground); break;
            case FileTool.CreatePdf: Jobs.Run($"{tool.Title()}: {paths.Count} images", paths, _ => Task.FromResult<IEnumerable<string>>([Pdf.CreateFromImageFiles(paths)])); break;
            case FileTool.MergePdf: Jobs.Run($"{tool.Title()}: {paths.Count} files", paths, _ => Task.FromResult<IEnumerable<string>>([Pdf.Merge(paths)])); break;
            case FileTool.JoinVideos: Jobs.Run($"{tool.Title()}: {paths.Count} videos", paths, async _ => [await Media.JoinVideos(paths)]); break;
            case FileTool.ReadQRCode:
                _ = Task.Run(() => paths.Select(p => (p, codes: Safe(() => Images.ReadCodes(p)))).ToList())
                    .ContinueWith(t => App.Ui(() => ResultWindows.ShowCodes(t.Result.SelectMany(r => r.codes.Select(c => (r.p, c))).ToList())));
                break;
            case FileTool.ExtractText:
                _ = Task.Run(async () =>
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var p in paths)
                    {
                        if (paths.Count > 1) sb.AppendLine($"— {Path.GetFileName(p)} —");
                        sb.AppendLine(await ExtractText(p));
                    }
                    return sb.ToString();
                }).ContinueWith(t => App.Ui(() =>
                {
                    var text = t.IsFaulted ? t.Exception!.InnerException!.Message : t.Result;
                    Ui.Copy(text);
                    ResultWindows.ShowText(text, paths[0]);
                }));
                break;
        }
    }

    static List<string> Safe(Func<List<string>> f) { try { return f(); } catch { return []; } }

    public static async Task<string> ExtractText(string path)
    {
        if (Formats.From(path) == ConversionFormat.Pdf)
        {
            var html = await Documents.ReadHtml(path);
            return Documents.HtmlToText(html);
        }
        if (Core.Platform.Ocr == null) throw new TangeloException("Windows OCR isn't available. Add a language with OCR support in Settings → Time & language.");
        using var img = Images.Load(path);
        return string.Join("\n", await Core.Platform.Ocr(img));
    }

    public static void Perform(FileAction action, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        switch (action)
        {
            case FileAction.Share:
                var host = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is not WheelWindow) ?? App.Host;
                try { Native.Share(host, paths); }
                catch (Exception e) { App.Alert("Sharing isn't available", e.Message); }
                break;
            case FileAction.Float: FloatManager.NewFloat(paths); break;
            case FileAction.CopyTo or FileAction.MoveTo:
                var dest = Ui.PickFolder($"Choose a destination folder for {(paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items")}");
                if (dest != null) Transfer(paths, dest, action == FileAction.MoveTo);
                break;
            case FileAction.CopyPath:
                Ui.Copy(string.Join(Environment.NewLine, paths.Select(p => paths.Count > 1 || p.Contains(' ') ? $"\"{p}\"" : p)));
                Feedback.Success();
                break;
            case FileAction.Terminal: OpenTerminal(Directory.Exists(paths[0]) ? paths[0] : Path.GetDirectoryName(paths[0])!); break;
            case FileAction.Preview: PreviewWindow.Show(paths); break;
            case FileAction.Reveal: Explorer.Reveal(paths); break;
        }
    }

    public static void Transfer(IReadOnlyList<string> paths, string dest, bool move)
    {
        Jobs.Run($"{(move ? "Moving" : "Copying")} {paths.Count} item{(paths.Count == 1 ? "" : "s")}", paths, progress =>
        {
            var outs = new List<string>();
            for (int i = 0; i < paths.Count; i++)
            {
                var p = paths[i];
                var isDir = Directory.Exists(p);
                var target = Output.Unique(dest, isDir ? Path.GetFileName(p) : Path.GetFileNameWithoutExtension(p), isDir ? "" : Path.GetExtension(p).TrimStart('.'));
                if (isDir) { if (move) Directory.Move(p, target); else CopyDirectory(p, target); }
                else if (move) File.Move(p, target); else File.Copy(p, target);
                outs.Add(target);
                progress.Report((i + 1.0) / paths.Count);
            }
            return Task.FromResult<IEnumerable<string>>(outs);
        });
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }

    static void OpenTerminal(string folder)
    {
        try
        {
            switch (Settings.Current.Terminal)
            {
                case "PowerShell": Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command Set-Location -LiteralPath '{folder.Replace("'", "''")}'") { UseShellExecute = true }); break;
                case "Command Prompt": Process.Start(new ProcessStartInfo("cmd.exe") { WorkingDirectory = folder, UseShellExecute = true }); break;
                default: Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true }); break;
            }
        }
        catch
        {
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoExit -Command Set-Location -LiteralPath '{folder.Replace("'", "''")}'") { UseShellExecute = true });
        }
    }

    // Menu items shared by the tray popup, floats and the chooser.
    public sealed record Item(string Id, string Title, string Glyph, Action<IReadOnlyList<string>> Run);

    public static List<Item> Items(WheelKind kind, IReadOnlyList<string> paths) => kind switch
    {
        WheelKind.Conversions => Converter.TargetsFor(paths).Select(f => new Item(f.ToString(), f.Label(), f.Category().Glyph(), p => Convert(p, f))).ToList(),
        WheelKind.Tools => ToolCatalog.Available(paths).Select(t => new Item(t.ToString(), t.Title(), t.Glyph(), p => RunTool(t, p))).ToList(),
        _ => Enum.GetValues<FileAction>().Select(a => new Item(a.ToString(), a.Title(), a.Glyph(), p => Perform(a, p))).ToList(),
    };
}

enum WheelKind { Conversions, Tools, Actions }
