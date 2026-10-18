using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tangelo.Core;

namespace Tangelo.App.Editors;

sealed class CompressEditor : EditorWindow
{
    enum Mode { Preset, TargetSize, ReduceBy }
    Mode mode = Mode.Preset;
    ImagePreset imagePreset = ImagePreset.Balanced;
    Media.VideoPreset videoPreset = Media.VideoPreset.Balanced;
    Media.AudioPreset audioPreset = Media.AudioPreset.Balanced;
    bool keepFormat = true;
    double pdfQuality = 60, pdfDpi = 120, target = 2, percent = 50;
    string unit = "MB";
    readonly ContentControl modeArea = new();
    readonly HashSet<FileCategory> cats;

    public CompressEditor(IReadOnlyList<string> paths) : base(FileTool.Compress, paths, "Compress")
    {
        cats = paths.Select(Formats.CategoryOf).ToHashSet();
        var files = Ui.Column(2, paths.Select(p =>
        {
            var d = new DockPanel();
            var size = Ui.Caption(Output.FormatBytes(Output.SizeOf(p)), false);
            DockPanel.SetDock(size, Dock.Right);
            d.Children.Add(size);
            d.Children.Add(Ui.Text(FileName(p), 12));
            return (UIElement)d;
        }).ToArray());
        SetContent(Ui.Scroll(Ui.Column(14,
            Ui.Segmented(Enum.GetValues<Mode>(), mode, m => m switch { Mode.TargetSize => "Target size", Mode.ReduceBy => "Reduce by %", _ => "Preset" }, m => { mode = m; Render(); }),
            modeArea, Ui.Text("Files", 13, FontWeights.SemiBold), Ui.Card(files))));
        Render();
    }

    static string Describe(ImagePreset p) => p switch
    {
        ImagePreset.High => "Better visual quality with moderate file-size savings.",
        ImagePreset.Balanced => "Smaller files with less quality loss.",
        ImagePreset.Small => "Smaller files with more quality loss.",
        _ => "Much smaller files with more visible quality loss.",
    };

    void Render()
    {
        var col = Ui.Column(8);
        switch (mode)
        {
            case Mode.Preset:
                if (cats.Contains(FileCategory.Image))
                {
                    var desc = Ui.Caption(Describe(imagePreset));
                    col.Children.Add(Ui.Labeled("Images", Ui.Combo(Enum.GetValues<ImagePreset>(), imagePreset, p => p.ToString(), p => { imagePreset = p; desc.Text = Describe(p); })));
                    col.Children.Add(desc);
                    col.Children.Add(Ui.Toggle("Keep original format", keepFormat, v => keepFormat = v));
                }
                if (cats.Contains(FileCategory.Video))
                    col.Children.Add(Ui.Labeled("Video", Ui.Combo(Enum.GetValues<Media.VideoPreset>(), videoPreset, p => p switch
                    {
                        Media.VideoPreset.High => "High (1080p HEVC)", Media.VideoPreset.Small => "Small (720p)", Media.VideoPreset.Smallest => "Smallest (540p)", _ => "Balanced (1080p)",
                    }, p => videoPreset = p)));
                if (cats.Contains(FileCategory.Audio))
                    col.Children.Add(Ui.Labeled("Audio", Ui.Combo(Enum.GetValues<Media.AudioPreset>(), audioPreset, p => p switch
                    {
                        Media.AudioPreset.High => "High (192 kbps)", Media.AudioPreset.Small => "Small (96 kbps)", Media.AudioPreset.Voice => "Voice (64 kbps mono)", _ => "Balanced (128 kbps)",
                    }, p => audioPreset = p)));
                if (cats.Contains(FileCategory.Pdf))
                {
                    col.Children.Add(Ui.Slider("PDF quality", 20, 90, pdfQuality, v => pdfQuality = v, v => $"{v:0}%", 1));
                    col.Children.Add(Ui.Slider("PDF resolution", 72, 200, pdfDpi, v => pdfDpi = v, v => $"{v:0} dpi", 1));
                }
                break;
            case Mode.TargetSize:
                col.Children.Add(Ui.Row(8, Ui.Field(target.ToString(), v => double.TryParse(v, out target), 90), Ui.Combo(["KB", "MB"], unit, u => u, u => unit = u)));
                col.Children.Add(Ui.Caption("The target is calculated separately for each file."));
                break;
            default:
                col.Children.Add(Ui.Slider("Reduce by", 10, 90, percent, v => percent = v, v => $"{v:0}%", 5));
                break;
        }
        modeArea.Content = col;
    }

    long? Target(string p) => mode switch
    {
        Mode.TargetSize => (long)(target * (unit == "MB" ? 1_000_000 : 1000)),
        Mode.ReduceBy => (long)(Output.SizeOf(p) * (1 - percent / 100)),
        _ => null,
    };

    protected override bool Apply()
    {
        var targets = Paths.ToDictionary(p => p, Target);
        var (ip, vp, ap, keep, q, dpi, preset) = (imagePreset, videoPreset, audioPreset, keepFormat, (int)pdfQuality, (int)pdfDpi, mode == Mode.Preset);
        Jobs.Batch(Tool, Paths, async p =>
        {
            var t = targets[p];
            if (t is < 1000) throw new TangeloException("Enter a target size of at least 1 KB.");
            if (t is { } tt && tt >= Output.SizeOf(p)) throw new TangeloException($"Target size cannot exceed the original file size ({Output.FormatBytes(Output.SizeOf(p))}).");
            return (IEnumerable<string>)[Formats.CategoryOf(p) switch
            {
                FileCategory.Image => Images.Compress(p, preset ? ip : null, t, keep),
                FileCategory.Video => await Media.CompressVideo(p, preset ? vp : null, t),
                FileCategory.Audio => await Media.CompressAudio(p, preset ? ap : null, t),
                FileCategory.Pdf => Pdf.Compress(p, q, dpi, t),
                _ => throw new TangeloException("This file type can't be compressed."),
            }];
        });
        return true;
    }
}

sealed class MetadataEditor : EditorWindow
{
    string title = "", author = "", copyright = "", description = "";
    bool removeGps = true;

    public MetadataEditor(IReadOnlyList<string> paths) : base(FileTool.Metadata, paths, "Remove All Metadata")
    {
        var col = Ui.Column(12);
        if (paths.Count == 1)
        {
            var search = Ui.Field("", _ => { }, double.NaN);
            var list = new StackPanel();
            List<(string Group, string Key, string Value)> fields = [];
            void Render(string q)
            {
                list.Children.Clear();
                foreach (var f in fields.Where(f => q.Length == 0 || f.Key.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Value.Contains(q, StringComparison.OrdinalIgnoreCase)))
                {
                    var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
                    g.ColumnDefinitions.Add(new ColumnDefinition());
                    var value = new TextBox { Text = f.Value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap };
                    var group = Ui.Caption(f.Group, false); var key = Ui.Text(f.Key, 12, FontWeights.SemiBold);
                    Grid.SetColumn(key, 1); Grid.SetColumn(value, 2);
                    g.Children.Add(group); g.Children.Add(key); g.Children.Add(value);
                    list.Children.Add(g);
                }
                if (fields.Count == 0) list.Children.Add(Ui.Caption("No readable metadata found."));
            }
            search.TextChanged += (_, _) => Render(search.Text);
            col.Children.Add(Ui.Labeled("Search fields", search, 100));
            var gps = Ui.Caption("", false); gps.Foreground = Theme.Accent;
            col.Children.Add(gps);
            col.Children.Add(Ui.Card(list));
            _ = Task.Run(async () => Formats.CategoryOf(paths[0]) switch
            {
                FileCategory.Image or FileCategory.Vector => Images.Metadata(paths[0]),
                FileCategory.Pdf => Pdf.Info(paths[0]).Select(kv => ("PDF", kv.Key, kv.Value)).ToList(),
                _ => (await Media.Metadata(paths[0])).Select(kv => ("Media", kv.Key, kv.Value)).ToList(),
            }).ContinueWith(t => Dispatcher.Invoke(() =>
            {
                fields = t.IsFaulted ? [] : t.Result;
                if (fields.Any(f => f.Group == "GPS")) gps.Text = "⚠ This file contains location data.";
                Render("");
            }));
        }
        else col.Children.Add(Ui.Caption($"{paths.Count} files selected. Remove All Metadata creates clean copies; originals stay unchanged."));

        if (paths.All(p => Formats.CategoryOf(p) == FileCategory.Image))
        {
            col.Children.Add(Ui.Text("Edit fields", 13, FontWeights.SemiBold));
            col.Children.Add(Ui.Card(Ui.Column(4,
                Ui.Labeled("Title", Ui.Field("", v => title = v), 100),
                Ui.Labeled("Author", Ui.Field("", v => author = v), 100),
                Ui.Labeled("Copyright", Ui.Field("", v => copyright = v), 100),
                Ui.Labeled("Description", Ui.Field("", v => description = v), 100),
                Ui.Row(12, Ui.Toggle("Remove location", true, v => removeGps = v),
                    Ui.Button("Save Edited Copy", () =>
                    {
                        var (t, a, c, d, g) = (title, author, copyright, description, removeGps);
                        Jobs.Batch(Tool, Paths, p => Images.EditMetadata(p, t, a, c, d, g));
                        Close();
                    })))));
        }
        SetContent(Ui.Scroll(col));
        SetOptions(Ui.Caption("Originals are never modified.", false));
    }

    protected override bool Apply()
    {
        Jobs.Batch(Tool, Paths, async p => (IEnumerable<string>)[Formats.CategoryOf(p) switch
        {
            FileCategory.Image or FileCategory.Vector => Images.StripMetadata(p),
            FileCategory.Pdf => Pdf.StripMetadata(p),
            _ => await Media.StripMetadata(p),
        }]);
        return true;
    }
}

sealed class RenameEditor : EditorWindow
{
    string pattern = "{name}", find = "", replace = "", caseMode = "Keep";
    int start = 1;
    bool sortByName = true;
    readonly StackPanel preview = new();
    readonly TextBlock warning = Ui.Caption("", false);

    public RenameEditor(IReadOnlyList<string> paths) : base(FileTool.BatchRename, paths, $"Rename {paths.Count} Files")
    {
        warning.Foreground = Theme.Accent;
        var form = Ui.Column(6,
            Ui.Labeled("Pattern", Ui.Field(pattern, v => { pattern = v; Render(); }), 110),
            Ui.Caption("Tokens: {name} {n} {date} {folder}"),
            Ui.Labeled("Find", Ui.Field("", v => { find = v; Render(); }), 110),
            Ui.Labeled("Replace with", Ui.Field("", v => { replace = v; Render(); }), 110),
            Ui.Labeled("Start at", Ui.Field("1", v => { int.TryParse(v, out start); Render(); }, 80), 110),
            Ui.Labeled("Case", Ui.Combo(["Keep", "lowercase", "UPPERCASE", "Title Case"], caseMode, c => c, c => { caseMode = c; Render(); }), 110),
            Ui.Toggle("Order the files by name, as File Explorer lists them", true, v => { sortByName = v; Render(); }));
        form.Width = 330;
        var dock = new DockPanel();
        DockPanel.SetDock(form, Dock.Left);
        dock.Children.Add(form);
        var scroll = Ui.Scroll(preview); scroll.Margin = new Thickness(16, 0, 0, 0);
        dock.Children.Add(scroll);
        SetContent(dock);
        SetOptions(warning);
        Render();
    }

    List<string> Ordered => sortByName ? Paths.OrderBy(Path.GetFileName, NaturalComparer.Instance).ToList() : Paths.ToList();

    string NewName(string path, int i)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var width = (Paths.Count + start).ToString().Length;
        var s = pattern.Replace("{name}", name).Replace("{n}", (start + i).ToString().PadLeft(width, '0'))
            .Replace("{date}", File.GetCreationTime(path).ToString("yyyy-MM-dd")).Replace("{folder}", Path.GetFileName(Path.GetDirectoryName(path)) ?? "");
        if (find.Length > 0) s = s.Replace(find, replace);
        s = caseMode switch
        {
            "lowercase" => s.ToLowerInvariant(),
            "UPPERCASE" => s.ToUpperInvariant(),
            "Title Case" => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower()),
            _ => s,
        };
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '-');
        return s.Trim() + ext;
    }

    bool Valid(out string why)
    {
        var names = Ordered.Select((p, i) => NewName(p, i).ToLowerInvariant()).ToList();
        if (names.Distinct().Count() != names.Count) { why = "Two files would get the same name."; return false; }
        if (names.Any(n => n.StartsWith('.') || Path.GetFileNameWithoutExtension(n).Length == 0)) { why = "A new name would be empty."; return false; }
        why = ""; return true;
    }

    void Render()
    {
        preview.Children.Clear();
        var ordered = Ordered;
        for (int i = 0; i < ordered.Count; i++)
            preview.Children.Add(Ui.Column(0, Ui.Text(NewName(ordered[i], i), 13, FontWeights.SemiBold), Ui.Caption(Path.GetFileName(ordered[i]), false)));
        CanApply = Valid(out var why);
        warning.Text = why;
    }

    protected override bool Apply()
    {
        var pairs = Ordered.Select((p, i) => (From: p, To: Path.Combine(Path.GetDirectoryName(p)!, NewName(p, i)))).ToList();
        Jobs.Run($"Renaming {pairs.Count} files", Paths, _ =>
        {
            // Two-phase rename so swaps (a→b, b→a) work.
            var temps = new List<(string Tmp, string To)>();
            foreach (var (from, to) in pairs.Where(p => !string.Equals(p.From, p.To, StringComparison.Ordinal)))
            {
                var tmp = Path.Combine(Path.GetDirectoryName(from)!, $".tangelo-{Guid.NewGuid():N}");
                File.Move(from, tmp);
                temps.Add((tmp, to));
            }
            foreach (var (tmp, to) in temps)
            {
                var final = File.Exists(to) ? Output.Unique(Path.GetDirectoryName(to)!, Path.GetFileNameWithoutExtension(to), Path.GetExtension(to).TrimStart('.')) : to;
                File.Move(tmp, final);
            }
            return Task.FromResult<IEnumerable<string>>(pairs.Select(p => p.To).ToList());
        });
        return true;
    }
}

sealed class SplitPdfEditor : EditorWindow
{
    bool everyPage = true;
    string ranges = "";
    readonly int pages;
    readonly TextBlock error = Ui.Caption("", false);

    public SplitPdfEditor(IReadOnlyList<string> paths) : base(FileTool.SplitPdf, paths, "Split PDF")
    {
        pages = Pdf.PageCount(paths[0]);
        error.Foreground = Theme.Accent;
        var rangeField = Ui.Field("", v => { ranges = v; Validate(); }, 320);
        rangeField.IsEnabled = false;
        SetContent(Ui.Column(12,
            Ui.Text($"{pages} pages", 14, FontWeights.SemiBold),
            Ui.Segmented([true, false], true, e => e ? "One file per page" : "Custom ranges", e => { everyPage = e; rangeField.IsEnabled = !e; Validate(); }),
            Ui.Labeled("Ranges", rangeField, 80),
            Ui.Caption($"For example: 1-3, 4, 5-{Math.Max(pages, 5)}"),
            error));
    }

    void Validate()
    {
        var ok = everyPage || Pdf.ParseRanges(ranges, pages) != null;
        error.Text = ok || ranges.Length == 0 ? "" : $"Use page numbers between 1 and {pages}.";
        CanApply = ok;
    }

    protected override bool Apply()
    {
        var r = everyPage ? null : Pdf.ParseRanges(ranges, pages);
        var p = Paths[0];
        Jobs.Run($"Splitting {FileName(p)}", Paths, _ => Task.FromResult<IEnumerable<string>>(Pdf.Split(p, r)));
        return true;
    }
}

sealed class OrganizePdfEditor : EditorWindow
{
    sealed class Page { public int Index; public int Rotation; public System.Windows.Media.Imaging.BitmapSource? Thumb; }
    readonly List<Page> pages = [];
    readonly WrapPanel grid = new();
    readonly HashSet<int> selected = [];
    bool equalize;
    Page? dragging;

    public OrganizePdfEditor(IReadOnlyList<string> paths) : base(FileTool.OrganizePdf, paths, "Save Organized PDF")
    {
        var bar = Ui.Row(6,
            Ui.Button("Rotate Left", () => Rotate(-90)),
            Ui.Button("Rotate Right", () => Rotate(90)),
            Ui.Button("Delete", () => { pages.RemoveAll(p => selected.Contains(p.Index)); selected.Clear(); Render(); }),
            Ui.Button("Reverse", () => { pages.Reverse(); Render(); }),
            Ui.Toggle("Make all pages the same width", false, v => equalize = v));
        bar.Margin = new Thickness(0, 0, 0, 10);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(Ui.Scroll(grid));
        SetContent(dock);
        SetOptions(Ui.Caption("Drag pages to reorder. Ctrl-click to select several.", false));
        this.OnKey(Key.Delete, () => { pages.RemoveAll(p => selected.Contains(p.Index)); selected.Clear(); Render(); });
        var count = Pdf.PageCount(paths[0]);
        for (int i = 0; i < count; i++) pages.Add(new Page { Index = i });
        Render();
        _ = Task.Run(() =>
        {
            int i = 0;
            foreach (var img in Pdf.RenderPages(paths[0], 40))
            {
                using (img)
                {
                    var bmp = Ui.ToBitmap(img);
                    var idx = i++;
                    Dispatcher.Invoke(() => { pages.First(p => p.Index == idx).Thumb = bmp; Render(); });
                }
            }
        });
    }

    void Rotate(int deg)
    {
        foreach (var p in pages.Where(p => selected.Contains(p.Index))) p.Rotation = ((p.Rotation + deg) % 360 + 360) % 360;
        Render();
    }

    void Render()
    {
        grid.Children.Clear();
        foreach (var page in pages)
        {
            var image = new Image { Source = page.Thumb, Width = 110, Height = 140, Stretch = Stretch.Uniform, LayoutTransform = new RotateTransform(page.Rotation) };
            var tile = new Border
            {
                Margin = new Thickness(6), Padding = new Thickness(6), CornerRadius = new CornerRadius(8), AllowDrop = true,
                Background = selected.Contains(page.Index) ? Theme.AccentSoft : Brushes.Transparent,
                Child = Ui.Column(4, new Border { Child = image, Background = Brushes.White, Width = 120, Height = 150 }, Ui.Caption((page.Index + 1).ToString(), false)),
            };
            ((TextBlock)((StackPanel)tile.Child).Children[1]).HorizontalAlignment = HorizontalAlignment.Center;
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { if (!selected.Add(page.Index)) selected.Remove(page.Index); }
                else { selected.Clear(); selected.Add(page.Index); }
                Render();
            };
            tile.MouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                dragging = page;
                DragDrop.DoDragDrop(tile, new DataObject("TangeloPage", page.Index), DragDropEffects.Move);
                dragging = null;
            };
            tile.DragEnter += (_, _) =>
            {
                if (dragging == null || dragging == page) return;
                pages.Remove(dragging);
                pages.Insert(pages.IndexOf(page) + (pages.IndexOf(page) >= 0 ? 0 : 0), dragging);
                Render();
            };
            grid.Children.Add(tile);
        }
        CanApply = pages.Count > 0;
    }

    protected override bool Apply()
    {
        var order = pages.Select(p => p.Index).ToList();
        var rot = pages.ToDictionary(p => p.Index, p => p.Rotation);
        var (eq, path) = (equalize, Paths[0]);
        Jobs.Run($"Organizing {FileName(path)}", Paths, _ => Task.FromResult<IEnumerable<string>>([Pdf.Organize(path, order, rot, eq)]));
        return true;
    }
}
