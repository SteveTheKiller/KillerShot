using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Shell;
using KillerShot.Video;

namespace KillerShot.Cli;

/// <summary>
/// The command line for scripts, scheduled tasks and KillerMCP: <c>killershot
/// &lt;command&gt;</c>. The --cli switch and the separate console tool stay compatible. Each command
/// runs in its own process without the tray, the global shortcuts or the single-instance claim,
/// so it works beside a running KillerShot. Results go to stdout, a one-line reason goes to
/// stderr, and the exit code says what happened. Anything not given on the command line follows
/// the user's KillerShot settings.
/// </summary>
public static class Program
{
    internal const int ExitOk = 0;
    internal const int ExitFailed = 1;
    internal const int ExitUsage = 2;
    internal const int ExitCancelled = 3;

    private static TextWriter Out = Console.Out;
    private static TextWriter Err = Console.Error;

    /// <summary>Entry for KillerShot.Cli.exe. The command runs inside a KillerShot application
    /// object, so the region selector and the window picker look the way they do in the app.</summary>
    public static int RunConsole(string[] args)
    {
        App.PendingCli = args;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>Runs one command on the UI thread and calls <paramref name="exit"/> with its exit
    /// code when it finishes.</summary>
    internal static void Start(string[] args, bool attachConsole, Action<int> exit, IEnumerable<IAudioCaptureSource>? audioSources = null)
    {
        OpenConsole(attachConsole);
        _ = RunAndExitAsync(args, exit, audioSources);
    }

    private static async Task RunAndExitAsync(string[] args, Action<int> exit, IEnumerable<IAudioCaptureSource>? audioSources)
    {
        int code;
        try { code = await RunAsync(args, audioSources); }
        catch (CliException ex) { Err.WriteLine(ex.Message); code = ex.Code; }
        catch (Exception ex)
        {
            Err.WriteLine(string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : OneLine(ex.Message));
            code = ExitFailed;
        }
        try { Out.Flush(); Err.Flush(); } catch { }
        exit(code);
    }

    private static Task<int> RunAsync(string[] args, IEnumerable<IAudioCaptureSource>? audioSources)
    {
        // A command line process can end at any moment, so it leaves importing and reading text
        // in pictures to the app.
        CaptureLibrary.BackgroundWork = false;
        if (args.Length == 0)
        {
            PrintUsage(Err);
            return Task.FromResult(ExitUsage);
        }
        string[] rest = args[1..];
        switch (args[0].ToLowerInvariant())
        {
            case "help": case "--help": case "-h": case "/?":
                PrintUsage(Out);
                return Task.FromResult(ExitOk);
            case "version": case "--version":
                Options.Parse(rest, [], []).NoPositionals();
                Out.WriteLine(AppInfo.Version);
                return Task.FromResult(ExitOk);
            case "capture": return CaptureAsync(rest);
            case "qr": return QrAsync(rest);
            case "record": return RecordAsync(rest, audioSources);
            case "recent": return Task.FromResult(Recent(rest));
            case "open": return Task.FromResult(Open(rest));
            case "monitors": return Task.FromResult(Monitors(rest));
            case "windows": return Task.FromResult(Windows(rest));
            case "settings": return Task.FromResult(SettingsCommand(rest));
            default:
                throw new CliException($"Unknown command '{args[0]}'. Run killershot --help for the list.");
        }
    }

    private static async Task<int> QrAsync(string[] args)
    {
        var options = Options.Parse(args, [], ["json"]);
        if (options.Positionals.Count != 1) throw new CliException("qr needs one image path, or region.");
        BitmapSource image;
        if (options.Positionals[0].Equals("region", StringComparison.OrdinalIgnoreCase))
        {
            App.InitializeKit();
            var frozen = FrozenDesktop.Capture(false);
            var selected = await new RegionSelector(frozen).RunAsync();
            if (selected == null) return ExitCancelled;
            image = frozen.Crop(selected.Value) ?? throw new CliException("The selected region is empty.", ExitFailed);
        }
        else
        {
            string path = Path.GetFullPath(options.Positionals[0]);
            if (!File.Exists(path)) throw new CliException("The image file does not exist.", ExitFailed);
            image = QrImages.Load(await File.ReadAllBytesAsync(path));
        }
        var codes = await Task.Run(() => QrImages.Read(image));
        if (options.Has("json"))
        {
            Out.WriteLine(Json(writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("codes");
                foreach (var code in codes)
                {
                    var link = KillerTools.Qr.QrLink.Check(code.Text);
                    writer.WriteStartObject();
                    writer.WriteString("text", code.Text);
                    writer.WriteNumber("version", code.Version);
                    writer.WriteString("errorCorrection", code.ErrorCorrection.ToString());
                    writer.WriteStartArray("corners");
                    foreach (var corner in code.Corners)
                    {
                        writer.WriteStartObject(); writer.WriteNumber("x", corner.X); writer.WriteNumber("y", corner.Y); writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartObject("link");
                    writer.WriteString("disposition", link.Disposition.ToString());
                    writer.WriteString("url", link.Url?.AbsoluteUri);
                    writer.WriteStartArray("warnings");
                    foreach (string warning in link.Warnings) writer.WriteStringValue(warning);
                    writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject();
            }));
        }
        else foreach (var code in codes) Out.WriteLine(code.Text);
        return ExitOk;
    }

    // ------------------------------------------------------------------------------------------
    // capture
    // ------------------------------------------------------------------------------------------

    private static readonly string[] CaptureValues = ["mode", "monitor", "window", "rect", "out", "format", "quality", "delay"];
    private static readonly string[] CaptureFlags = ["json", "clipboard", "no-clipboard", "save", "no-save", "cursor", "no-cursor",
        "shutter", "no-shutter", "pick", "editor", "no-recent", "overwrite"];

    private static async Task<int> CaptureAsync(string[] args)
    {
        var o = Options.Parse(args, CaptureValues, CaptureFlags);
        o.NoPositionals();
        var mode = ModeFor(o, CaptureSettings.LastMode == CaptureMode.Qr ? CaptureMode.Region : CaptureSettings.LastMode);
        int delay = o.Int("delay", 0, 30) ?? CaptureSettings.DelaySeconds;
        bool cursor = o.Switch("cursor", "no-cursor") ?? CaptureSettings.IncludeCursor;
        bool shutter = o.Switch("shutter", "no-shutter") ?? CaptureSettings.PlaySound;
        bool copy = o.Switch("clipboard", "no-clipboard") ?? CaptureSettings.CopyToClipboard;
        bool? saveSwitch = o.Switch("save", "no-save");
        string? outArg = o.Value("out");
        if (outArg != null && saveSwitch == false) throw new CliException("--out and --no-save cannot be combined.");
        bool save = outArg != null || (saveSwitch ?? CaptureSettings.SaveToFolder);
        string? formatArg = o.Value("format") is { } f ? ImageFormat(f) : null;
        int quality = o.Int("quality", 10, 100) ?? CaptureSettings.JpegQuality;
        bool recent = !o.Has("no-recent");
        bool editor = o.Has("editor");
        if (!save && !copy && !recent) throw new CliException("capture needs at least one output: a saved file, the clipboard, or the library.");
        if (editor && !save && !recent) throw new CliException("--editor needs a saved file or the library; drop --no-save or --no-recent.");

        // Everything that can be checked before the capture is checked first, so a typo costs
        // nothing and never leaves a half-finished capture behind.
        var target = outArg != null ? OutputTarget.ForImage(outArg, formatArg, o.Has("overwrite")) : null;
        string format = target?.Format ?? formatArg ?? CaptureSettings.Format;
        Win32.RECT? rect = o.Value("rect") is { } r ? ParseRect(r) : null;
        IntPtr window = mode == CaptureMode.Window && o.Value("window") is { } w ? ResolveWindow(w) : IntPtr.Zero;
        var monitor = mode == CaptureMode.Monitor ? ResolveMonitor(o.Value("monitor")) : null;
        bool pick = o.Has("pick");
        if (recent && !save && !copy && !CaptureLibrary.TryReadForCli(() => { }))
            throw new CliException(LibraryClosedReason(), ExitFailed);
        if ((mode == CaptureMode.Region && rect == null) || pick) App.InitializeKit();

        if (delay > 0) await Task.Delay(delay * 1000);
        if (mode == CaptureMode.Window && window == IntPtr.Zero && !pick)
        {
            window = WindowCapture.CapturableRoot(Win32.GetForegroundWindow());
            if (window == IntPtr.Zero)
                throw new CliException("No capturable window is in front. Pass --window with a handle or title, or --pick.", ExitFailed);
        }

        var frozen = FrozenDesktop.Capture(cursor);
        if (shutter) ShutterSound.Play();
        BitmapSource? bmp = null;
        bool usedGraphicsCapture = false;
        switch (mode)
        {
            case CaptureMode.Region:
                if (rect is { } wanted)
                {
                    var clipped = Win32.RECT.Intersect(wanted, frozen.Bounds);
                    if (clipped.IsEmpty) throw new CliException("The --rect rectangle is outside the desktop.", ExitFailed);
                    bmp = frozen.Crop(clipped);
                }
                else
                {
                    var selected = await new RegionSelector(frozen).RunAsync();
                    if (selected != null) bmp = frozen.Crop(selected.Value);
                }
                break;
            case CaptureMode.Window:
                if (pick)
                {
                    var windowPicker = new RegionSelector(frozen, windowOnly: true);
                    var clicked = await windowPicker.RunAsync();
                    if (clicked == null) break;
                    if (windowPicker.PickedWindow == IntPtr.Zero) { bmp = frozen.Crop(clicked.Value); break; }
                    window = windowPicker.PickedWindow;
                }
                (bmp, usedGraphicsCapture) = await WindowCapture.CaptureAsync(window, frozen);
                if (bmp == null) throw new CliException("The window could not be captured.", ExitFailed);
                break;
            case CaptureMode.Monitor:
                if (monitor == null)
                {
                    Win32.GetCursorPos(out var p);
                    monitor = frozen.MonitorAt(p);
                }
                bmp = frozen.Crop(monitor.Bounds);
                break;
            default:
                bmp = frozen.Crop(frozen.Bounds);
                break;
        }
        if (bmp == null)
        {
            Err.WriteLine("Capture cancelled.");
            return ExitCancelled;
        }

        var taken = DateTime.Now;
        var warnings = new List<string>();
        long? libraryId = null;
        if (recent)
        {
            try
            {
                IntPtr source = window != IntPtr.Zero ? window : IntPtr.Zero;
                if (!CaptureLibrary.TryAddNowFromCli(bmp, taken, ModeName(mode),
                    source != IntPtr.Zero ? Win32.WindowTitle(source) : null, Win32.ProcessName(source), out long stored))
                    throw new InvalidOperationException(LibraryClosedReason());
                libraryId = stored;
            }
            catch (Exception ex) { warnings.Add("The capture was not added to the library: " + OneLine(ex.Message)); }
        }
        string? libraryRef = libraryId is long lid ? LibraryRef(lid) : null;

        string? saved = null;
        if (save)
        {
            if (target?.File is { } file)
            {
                ImageOutput.Save(bmp, file, quality, o.Has("overwrite"));
                saved = file;
            }
            else
            {
                string name = OutputFiles.FileName(CaptureSettings.FileNamePattern, taken, mode, bmp.PixelWidth, bmp.PixelHeight, format);
                saved = ImageOutput.SaveUnique(bmp, target?.Folder ?? CaptureSettings.OutputFolder, name, quality);
            }
        }

        bool copied = false;
        string? copyError = null;
        if (copy) copied = ImageOutput.TryCopy(bmp, out copyError);
        if (editor) OpenInEditor((saved ?? libraryRef)!);

        if (o.Has("json"))
        {
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                j.WriteString("path", saved);
                if (libraryId is long stored) j.WriteNumber("libraryId", stored);
                else j.WriteNull("libraryId");
                j.WriteString("mode", ModeName(mode));
                j.WriteNumber("width", bmp.PixelWidth);
                j.WriteNumber("height", bmp.PixelHeight);
                j.WriteBoolean("clipboard", copied);
                if (mode == CaptureMode.Monitor && monitor != null) j.WriteNumber("monitor", MonitorNumber(monitor));
                if (window != IntPtr.Zero)
                {
                    j.WriteString("window", Handle(window));
                    j.WriteString("title", Win32.WindowTitle(window));
                    j.WriteBoolean("graphicsCapture", usedGraphicsCapture);
                }
                j.WriteString("taken", taken.ToString("O", CultureInfo.InvariantCulture));
                j.WriteEndObject();
            }));
        }
        else if ((saved ?? libraryRef) is { } shown)
        {
            Out.WriteLine(shown);
        }

        foreach (var warning in warnings) Err.WriteLine(warning);
        if (copy && !copied)
        {
            Err.WriteLine("The clipboard is busy: " + OneLine(copyError ?? "another app is holding it"));
            return ExitFailed;
        }
        return libraryId != null || saved != null || copied ? ExitOk : ExitFailed;
    }

    // ------------------------------------------------------------------------------------------
    // record
    // ------------------------------------------------------------------------------------------

    private static readonly string[] RecordValues = ["mode", "monitor", "window", "rect", "out", "format", "seconds", "fps", "sound",
        "quality", "gif-colors", "gif-size"];
    private static readonly string[] RecordFlags = ["json", "cursor", "no-cursor", "clipboard", "pick", "overwrite", "loop", "no-loop"];

    private static async Task<int> RecordAsync(string[] args, IEnumerable<IAudioCaptureSource>? audioSources)
    {
        var o = Options.Parse(args, RecordValues, RecordFlags);
        o.NoPositionals();
        int seconds = o.Int("seconds", 1, 3600) ?? throw new CliException("record needs --seconds with a value from 1 to 3600.");
        var mode = ModeFor(o, CaptureMode.Region);
        string? formatArg = o.Value("format") is { } f ? VideoFormat(f) : null;
        var target = o.Value("out") is { } outArg ? OutputTarget.ForVideo(outArg, formatArg, o.Has("overwrite")) : null;
        string format = target?.Format ?? formatArg ?? VideoSettings.Format;
        bool gif = format == "gif";

        int fps = o.Int("fps", 1, 60) ?? (gif ? VideoSettings.GifFps : VideoSettings.Mp4Fps);
        var soundArg = o.Value("sound") is { } s ? ParseSound(s) : (SoundSource?)null;
        if (gif && soundArg is { } given && given != SoundSource.None) throw new CliException("GIF recordings have no sound; drop --sound or record MP4.");
        var sound = gif ? SoundSource.None : soundArg ?? VideoSettings.Sound;
        double bitsPerPixel = o.Value("quality") is { } q ? Mp4Quality(q) : VideoSettings.Mp4BitsPerPixel;
        int gifColors = o.Int("gif-colors", 2, 256) ?? VideoSettings.GifColors;
        int gifSize = o.Int("gif-size", 10, 100) ?? VideoSettings.GifSize;
        bool loop = o.Switch("loop", "no-loop") ?? VideoSettings.GifLoop;
        bool cursor = o.Switch("cursor", "no-cursor") ?? VideoSettings.IncludeCursor;

        Win32.RECT? rect = o.Value("rect") is { } r ? ParseRect(r) : null;
        IntPtr window = mode == CaptureMode.Window && o.Value("window") is { } w ? ResolveWindow(w) : IntPtr.Zero;
        var monitor = mode == CaptureMode.Monitor ? ResolveMonitor(o.Value("monitor")) : null;
        bool pick = o.Has("pick");
        var desktop = VirtualDesktop();

        Win32.RECT area;
        switch (mode)
        {
            case CaptureMode.Region when rect == null:
            case CaptureMode.Window when pick:
                App.InitializeKit();
                var selector = new RegionSelector(FrozenDesktop.Capture(), windowOnly: mode == CaptureMode.Window);
                if (await selector.RunAsync() is not { } picked)
                {
                    Err.WriteLine("Recording cancelled.");
                    return ExitCancelled;
                }
                if (selector.PickedWindow != IntPtr.Zero) window = selector.PickedWindow;
                area = picked;
                break;
            case CaptureMode.Region:
                area = rect!.Value;
                break;
            case CaptureMode.Window:
                if (window == IntPtr.Zero) window = WindowCapture.CapturableRoot(Win32.GetForegroundWindow());
                if (window == IntPtr.Zero || WindowCapture.FrameBounds(window) is not { } frame)
                    throw new CliException("No capturable window is in front. Pass --window with a handle or title, or --pick.", ExitFailed);
                area = frame;
                break;
            case CaptureMode.Monitor:
                if (monitor == null)
                {
                    Win32.GetCursorPos(out var p);
                    monitor = FrozenDesktop.EnumerateMonitors().FirstOrDefault(m => m.Bounds.Contains(p))
                        ?? FrozenDesktop.EnumerateMonitors().First(m => m.Primary);
                }
                area = monitor.Bounds;
                break;
            default:
                area = desktop;
                break;
        }
        area = Win32.RECT.Intersect(area, desktop);
        if (area.IsEmpty) throw new CliException("The area to record is outside the desktop.", ExitFailed);
        if (!gif)
        {
            var (ew, eh) = Mp4Encoder.EvenSize(area.Width, area.Height);
            area = new Win32.RECT(area.Left, area.Top, area.Left + ew, area.Top + eh);
        }

        string ext = gif ? "gif" : "mp4";
        string path = target?.File ?? "";
        string outputFolder = target?.File is { } explicitFile ? Path.GetDirectoryName(explicitFile)!
            : target?.Folder ?? VideoSettings.OutputFolder;
        string fileName = OutputFiles.FileName(VideoSettings.FileNamePattern, DateTime.Now, mode, area.Width, area.Height, ext);
        string? stageFolder = null;
        string? recordingPath = null;
        bool recordingComplete = false;
        Exception? recordingWarning = null;
        bool published = false;
        IVideoSink sink = null!;
        Recorder recorder = null!;
        TimeSpan duration;
        var stop = new TaskCompletionSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.TrySetResult(); };
        try
        {
            stageFolder = Path.Combine(outputFolder, $".KillerShot-staging-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stageFolder);
            try { File.SetAttributes(stageFolder, File.GetAttributes(stageFolder) | FileAttributes.Hidden); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            recordingPath = OutputFiles.NextPath(stageFolder, $"recording.{ext}");
            sink = gif
                ? new GifEncoder(recordingPath, gifColors >= 256 ? 255 : gifColors, gifSize / 100.0, loop)
                : new Mp4Encoder(recordingPath, bitsPerPixel, sound == SoundSource.None ? 0 : VideoSettings.AudioKbps * 1000);
            recorder = new Recorder(area, sink, fps, cursor, sound, audioSources);
            Console.CancelKeyPress += onCancel;
            await recorder.StartAsync();
            await Task.WhenAny(Task.Delay(seconds * 1000), stop.Task);
            duration = recorder.Elapsed;
            try { await recorder.StopAsync(); }
            catch (Exception ex) when (recorder.IsFinalized) { recordingWarning = ex; }
            recordingComplete = true;
            if (target?.File == null) path = OutputFiles.PublishUnique(recordingPath, outputFolder, fileName);
            else if (o.Has("overwrite")) File.Move(recordingPath, path, true);
            else File.Move(recordingPath, path);
            published = true;
        }
        catch (Exception ex)
        {
            try { sink?.Dispose(); } catch { }
            if (recordingComplete)
                throw new IOException($"The completed recording could not be saved to {target?.File ?? Path.Combine(outputFolder, fileName)}. It remains at {recordingPath}. {ex.Message}", ex);
            if (recordingPath != null) TryDelete(recordingPath);
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
            if (stageFolder != null && (!recordingComplete || published))
                try { Directory.Delete(stageFolder); } catch { }
        }

        bool copied = false;
        if (recordingWarning != null) Err.WriteLine("Recording saved with a warning: " + OneLine(recordingWarning.Message));
        if (o.Has("clipboard")) copied = CopyFileDrop(path);

        if (o.Has("json"))
        {
            int width = sink is GifEncoder g ? g.OutputWidth : sink is Mp4Encoder m ? m.OutputWidth : area.Width;
            int height = sink is GifEncoder g2 ? g2.OutputHeight : sink is Mp4Encoder m2 ? m2.OutputHeight : area.Height;
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                j.WriteString("path", path);
                j.WriteString("format", format);
                j.WriteString("mode", ModeName(mode));
                j.WriteNumber("width", width);
                j.WriteNumber("height", height);
                j.WriteNumber("fps", fps);
                j.WriteNumber("seconds", Math.Round(duration.TotalSeconds, 2));
                j.WriteNumber("frames", recorder.Frames);
                j.WriteNumber("dropped", recorder.Dropped);
                j.WriteString("sound", SoundName(sound));
                j.WriteBoolean("clipboard", copied);
                if (window != IntPtr.Zero) j.WriteString("window", Handle(window));
                j.WriteEndObject();
            }));
        }
        else
        {
            Out.WriteLine(path);
        }

        if (o.Has("clipboard") && !copied)
        {
            Err.WriteLine("The clipboard is busy, so the file was not copied.");
            return ExitFailed;
        }
        return ExitOk;
    }

    // ------------------------------------------------------------------------------------------
    // recent, open, monitors, windows, settings
    // ------------------------------------------------------------------------------------------

    private static int Recent(string[] args)
    {
        var o = Options.Parse(args, ["limit", "search", "copy", "export", "open", "out"], ["json", "overwrite"]);
        o.NoPositionals();
        string[] actions = ["copy", "export", "open"];
        var chosen = actions.Where(o.Has).ToArray();
        if (chosen.Length > 1) throw new CliException("Use one of --copy, --export and --open at a time.");
        if (o.Has("out") && (chosen.Length == 0 || chosen[0] != "export")) throw new CliException("--out goes with --export.");
        string? action = chosen.Length == 0 ? null : chosen[0];
        if (action != null && o.Has("limit")) throw new CliException("--limit only applies to the list.");
        int limit = action == null ? o.Int("limit", 1, RecentListMax) ?? RecentListDefault : 0;
        int index = action == null ? 0 : o.Int(action, 1, int.MaxValue)!.Value;
        OutputTarget? target = action == "export"
            ? OutputTarget.ForImage(o.Value("out") ?? throw new CliException("--export needs --out with a file or folder."),
                null, o.Has("overwrite"))
            : null;
        bool imageExport = action == "export" && target!.File != null && target.Format != "png";

        IReadOnlyList<CaptureItem> items = [];
        Dictionary<long, string> collections = new();
        CaptureItem? selected = null;
        BitmapSource? image = null;
        byte[]? png = null;
        if (!CaptureLibrary.TryReadForCli(() =>
        {
            items = CaptureLibrary.Items(o.Value("search"));
            if (action == null)
            {
                collections = CaptureLibrary.Collections().ToDictionary(c => c.Id, c => c.Name);
                return;
            }
            if (index > items.Count)
                throw new CliException(items.Count == 0 ? "The library is empty." : $"There are only {items.Count} captures.", ExitFailed);
            selected = items[index - 1];
            if (action == "copy" || imageExport) image = CaptureLibrary.LoadImage(selected.Id);
            else if (action == "export") png = CaptureLibrary.LoadPng(selected.Id);
        })) throw new CliException(LibraryClosedReason(), ExitFailed);

        if (action == null)
        {
            var shown = items.Take(limit).ToList();
            if (o.Has("json"))
            {
                Out.WriteLine(Json(j =>
                {
                    j.WriteStartObject();
                    j.WriteNumber("total", items.Count);
                    j.WriteStartArray("items");
                    for (int i = 0; i < shown.Count; i++)
                    {
                        var c = shown[i];
                        j.WriteStartObject();
                        j.WriteNumber("index", i + 1);
                        j.WriteNumber("id", c.Id);
                        j.WriteString("taken", c.Taken.ToString("O", CultureInfo.InvariantCulture));
                        j.WriteNumber("width", c.Width);
                        j.WriteNumber("height", c.Height);
                        j.WriteString("mode", c.Mode.Length > 0 ? c.Mode : null);
                        j.WriteString("title", c.Window);
                        j.WriteString("program", c.Process);
                        j.WriteString("collection", c.CollectionId is long cid && collections.TryGetValue(cid, out var name) ? name : null);
                        j.WriteString("note", c.Note);
                        j.WriteEndObject();
                    }
                    j.WriteEndArray();
                    j.WriteEndObject();
                }));
            }
            else
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    var c = shown[i];
                    string label = c.Window ?? c.Process ?? "";
                    Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i + 1,3}  {c.Taken:yyyy-MM-dd HH:mm:ss}  {c.Width}x{c.Height}  {label}").TrimEnd());
                }
            }
            return ExitOk;
        }

        var item = selected!;
        string result = LibraryRef(item.Id);
        switch (action)
        {
            case "copy":
                if (!ImageOutput.TryCopy(image!, out var error))
                    throw new CliException("The clipboard is busy: " + OneLine(error ?? "another app is holding it"), ExitFailed);
                break;
            case "export":
                if (target!.File is { } exportFile && imageExport)
                {
                    ImageOutput.Save(image!, exportFile, CaptureSettings.JpegQuality, o.Has("overwrite"));
                    result = exportFile;
                }
                else
                {
                    if (target.File is { } pngFile)
                    {
                        OutputFiles.WriteAtomic(pngFile, o.Has("overwrite"), stream => stream.Write(png!));
                        result = pngFile;
                    }
                    else
                    {
                        string name = OutputFiles.FileName(CaptureSettings.FileNamePattern, item.Taken,
                            ModeFromKey(item.Mode), item.Width, item.Height, "png");
                        result = OutputFiles.WriteUnique(target.Folder!, name, stream => stream.Write(png!));
                    }
                }
                break;
            case "open":
                OpenInEditor(result);
                break;
        }
        if (o.Has("json"))
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                j.WriteNumber("index", index);
                j.WriteNumber("id", item.Id);
                if (action == "export") j.WriteString("path", result);
                j.WriteString("action", action);
                j.WriteEndObject();
            }));
        else
            Out.WriteLine(result);
        return ExitOk;
    }

    private const int RecentListDefault = 20;
    private const int RecentListMax = 1000;

    /// <summary>How the editor and the command line name a library capture: library:id.</summary>
    private static string LibraryRef(long id) => "library:" + id.ToString(CultureInfo.InvariantCulture);

    private static string LibraryClosedReason() => CaptureLibrary.NeedsPassword
        ? "The command line cannot open password-protected libraries."
        : "The library could not be opened: " + OneLine(CaptureLibrary.LoadError ?? "unknown error");

    private static CaptureMode ModeFromKey(string key) => key switch
    {
        "window" => CaptureMode.Window,
        "monitor" => CaptureMode.Monitor,
        "all" => CaptureMode.AllMonitors,
        _ => CaptureMode.Region,
    };

    private static int Open(string[] args)
    {
        var o = Options.Parse(args, [], ["json"]);
        if (o.Positionals.Count != 1) throw new CliException("open needs one image path.");
        string path = Path.GetFullPath(o.Positionals[0]);
        if (!File.Exists(path)) throw new CliException("The image does not exist: " + path, ExitFailed);
        try { ImageOutput.Load(path); }
        catch (Exception ex) { throw new CliException("KillerShot cannot read that image: " + OneLine(ex.Message), ExitFailed); }
        OpenInEditor(path);
        if (o.Has("json")) Out.WriteLine(Json(j => { j.WriteStartObject(); j.WriteString("opened", path); j.WriteEndObject(); }));
        else Out.WriteLine(path);
        return ExitOk;
    }

    private static int Monitors(string[] args)
    {
        var o = Options.Parse(args, [], ["json"]);
        o.NoPositionals();
        var list = OrderedMonitors();
        if (o.Has("json"))
        {
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                j.WriteStartArray("monitors");
                for (int i = 0; i < list.Count; i++)
                {
                    j.WriteStartObject();
                    j.WriteNumber("index", i + 1);
                    j.WriteBoolean("primary", list[i].Primary);
                    WriteBounds(j, list[i].Bounds);
                    j.WriteEndObject();
                }
                j.WriteEndArray();
                j.WriteEndObject();
            }));
        }
        else
        {
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i].Bounds;
                Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i + 1}  {(list[i].Primary ? "primary" : "       ")}  {b.Left},{b.Top}  {b.Width}x{b.Height}"));
            }
        }
        return ExitOk;
    }

    private static int Windows(string[] args)
    {
        var o = Options.Parse(args, ["limit"], ["json"]);
        o.NoPositionals();
        int limit = o.Int("limit", 1, 500) ?? 100;
        var all = FrozenDesktop.EnumerateWindows();
        var shown = all.Take(limit).ToList();
        if (o.Has("json"))
        {
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                j.WriteStartArray("windows");
                foreach (var w in shown)
                {
                    j.WriteStartObject();
                    j.WriteString("handle", Handle(w.Handle));
                    j.WriteString("title", Win32.WindowTitle(w.Handle));
                    j.WriteString("process", ProcessName(w.Handle));
                    WriteBounds(j, w.Bounds);
                    j.WriteEndObject();
                }
                j.WriteEndArray();
                j.WriteBoolean("limitReached", all.Count > shown.Count);
                j.WriteEndObject();
            }));
        }
        else
        {
            foreach (var w in shown)
                Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Handle(w.Handle),-12}{w.Bounds.Width,5}x{w.Bounds.Height,-5}  {ProcessName(w.Handle),-20}  {Win32.WindowTitle(w.Handle)}"));
        }
        return ExitOk;
    }

    private static int SettingsCommand(string[] args)
    {
        var o = Options.Parse(args, [], ["json"]);
        o.NoPositionals();
        var capture = new (string Key, object Value)[]
        {
            ("lastMode", ModeName(CaptureSettings.LastMode)),
            ("copyToClipboard", CaptureSettings.CopyToClipboard),
            ("saveToFolder", CaptureSettings.SaveToFolder),
            ("outputFolder", CaptureSettings.OutputFolder),
            ("fileNamePattern", CaptureSettings.FileNamePattern),
            ("format", CaptureSettings.Format),
            ("jpegQuality", CaptureSettings.JpegQuality),
            ("delaySeconds", CaptureSettings.DelaySeconds),
            ("includeCursor", CaptureSettings.IncludeCursor),
            ("playSound", CaptureSettings.PlaySound),
            ("pickWindow", CaptureSettings.PickWindow),
            ("askToSave", CaptureSettings.AskToSave),
            ("openEditorAfterCapture", CaptureSettings.OpenEditorAfterCapture),
            ("notify", CaptureSettings.Notify),
        };
        var video = new (string Key, object Value)[]
        {
            ("format", VideoSettings.Format),
            ("mp4Fps", VideoSettings.Mp4Fps),
            ("mp4Quality", VideoSettings.Mp4Quality switch { 0 => "small", 2 => "high", _ => "balanced" }),
            ("gifFps", VideoSettings.GifFps),
            ("gifColors", VideoSettings.GifColors),
            ("gifSize", VideoSettings.GifSize),
            ("gifLoop", VideoSettings.GifLoop),
            ("sound", SoundName(VideoSettings.Sound)),
            ("audioKbps", VideoSettings.AudioKbps),
            ("includeCursor", VideoSettings.IncludeCursor),
            ("countdown", VideoSettings.Countdown),
            ("copyFile", VideoSettings.CopyFile),
            ("outputFolder", VideoSettings.OutputFolder),
            ("fileNamePattern", VideoSettings.FileNamePattern),
        };
        var shortcuts = Enum.GetValues<ShortcutAction>().Select(a => (Key: ShortcutName(a), Value: (object)CaptureSettings.ShortcutText(a))).ToArray();

        if (o.Has("json"))
        {
            Out.WriteLine(Json(j =>
            {
                j.WriteStartObject();
                foreach (var (name, items) in new[] { ("capture", capture), ("recording", video), ("shortcuts", shortcuts) })
                {
                    j.WriteStartObject(name);
                    foreach (var (key, value) in items)
                    {
                        switch (value)
                        {
                            case bool b: j.WriteBoolean(key, b); break;
                            case int n: j.WriteNumber(key, n); break;
                            default: j.WriteString(key, value.ToString()); break;
                        }
                    }
                    j.WriteEndObject();
                }
                j.WriteEndObject();
            }));
        }
        else
        {
            foreach (var (name, items) in new[] { ("capture", capture), ("recording", video), ("shortcuts", shortcuts) })
                foreach (var (key, value) in items)
                    Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}.{key} = {(value is bool b ? (b ? "true" : "false") : value)}"));
        }
        return ExitOk;
    }

    // ------------------------------------------------------------------------------------------
    // Targets and parsing
    // ------------------------------------------------------------------------------------------

    /// <summary>Where --out points: one file, or a folder that gets the usual file name.</summary>
    private sealed record OutputTarget(string? File, string? Folder, string? Format)
    {
        public static OutputTarget ForImage(string value, string? format, bool overwrite) =>
            Resolve(value, format, overwrite, ext => ext switch { ".png" => "png", ".jpg" or ".jpeg" => "jpg", _ => null }, ".png, .jpg or .jpeg");

        public static OutputTarget ForVideo(string value, string? format, bool overwrite) =>
            Resolve(value, format, overwrite, ext => ext switch { ".mp4" => "mp4", ".gif" => "gif", _ => null }, ".mp4 or .gif");

        private static OutputTarget Resolve(string value, string? format, bool overwrite, Func<string, string?> formatOf, string allowed)
        {
            string full;
            try { full = Path.GetFullPath(value); }
            catch (Exception ex) { throw new CliException("--out is not a valid path: " + OneLine(ex.Message)); }
            if (Directory.Exists(full) || value.EndsWith('\\') || value.EndsWith('/'))
                return new OutputTarget(null, full, format);
            string? fileFormat = formatOf(Path.GetExtension(full).ToLowerInvariant());
            if (fileFormat == null) throw new CliException($"The --out file must end in {allowed}.");
            if (format != null && format != fileFormat) throw new CliException($"--format {format} does not match the --out file's extension.");
            string? folder = Path.GetDirectoryName(full);
            if (folder == null || !Directory.Exists(folder)) throw new CliException("The folder for --out does not exist: " + folder, ExitFailed);
            if (System.IO.File.Exists(full) && !overwrite) throw new CliException("The --out file already exists. Pass --overwrite to replace it.", ExitFailed);
            return new OutputTarget(full, null, fileFormat);
        }
    }

    /// <summary>The mode from --mode, or the one the target options imply, or the fallback.</summary>
    private static CaptureMode ModeFor(Options o, CaptureMode fallback)
    {
        var implied = new List<(string Option, CaptureMode Mode)>();
        if (o.Has("rect")) implied.Add(("rect", CaptureMode.Region));
        if (o.Has("window")) implied.Add(("window", CaptureMode.Window));
        if (o.Has("pick")) implied.Add(("pick", CaptureMode.Window));
        if (o.Has("monitor")) implied.Add(("monitor", CaptureMode.Monitor));
        if (implied.Select(i => i.Mode).Distinct().Count() > 1)
            throw new CliException($"--{implied[0].Option} and --{implied.First(i => i.Mode != implied[0].Mode).Option} cannot be combined.");
        if (o.Has("window") && o.Has("pick")) throw new CliException("--window and --pick cannot be combined.");
        if (o.Value("mode") is { } text)
        {
            var mode = ParseMode(text);
            if (implied.Count > 0 && implied[0].Mode != mode) throw new CliException($"--{implied[0].Option} does not apply to --mode {text}.");
            return mode;
        }
        return implied.Count > 0 ? implied[0].Mode : fallback;
    }

    private static CaptureMode ParseMode(string text) => text.ToLowerInvariant() switch
    {
        "region" => CaptureMode.Region,
        "window" => CaptureMode.Window,
        "monitor" => CaptureMode.Monitor,
        "all" => CaptureMode.AllMonitors,
        _ => throw new CliException("--mode must be region, window, monitor or all."),
    };

    private static string ModeName(CaptureMode mode) => mode switch
    {
        CaptureMode.Qr => "qr",
        CaptureMode.Region => "region",
        CaptureMode.Window => "window",
        CaptureMode.Monitor => "monitor",
        _ => "all",
    };

    private static string ImageFormat(string text) => text.ToLowerInvariant() switch
    {
        "png" => "png",
        "jpg" or "jpeg" => "jpg",
        _ => throw new CliException("--format must be png or jpg."),
    };

    private static string VideoFormat(string text) => text.ToLowerInvariant() switch
    {
        "mp4" => "mp4",
        "gif" => "gif",
        _ => throw new CliException("--format must be mp4 or gif."),
    };

    private static SoundSource ParseSound(string text) => text.ToLowerInvariant() switch
    {
        "none" => SoundSource.None,
        "system" => SoundSource.System,
        "mic" or "microphone" => SoundSource.Microphone,
        "both" => SoundSource.Both,
        _ => throw new CliException("--sound must be none, system, mic or both."),
    };

    private static string SoundName(SoundSource sound) => sound switch
    {
        SoundSource.System => "system",
        SoundSource.Microphone => "mic",
        SoundSource.Both => "both",
        _ => "none",
    };

    private static double Mp4Quality(string text) => text.ToLowerInvariant() switch
    {
        "small" => 0.04,
        "balanced" => 0.09,
        "high" => 0.2,
        _ => throw new CliException("--quality must be small, balanced or high."),
    };

    private static string ShortcutName(ShortcutAction action) => action switch
    {
        ShortcutAction.LastMode => "lastMode",
        ShortcutAction.Window => "window",
        ShortcutAction.Monitor => "monitor",
        ShortcutAction.AllMonitors => "all",
        _ => "record",
    };

    private static Win32.RECT ParseRect(string text)
    {
        var parts = text.Split(',');
        if (parts.Length == 4
            && int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
            && int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)
            && int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)
            && int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            && w > 0 && h > 0)
            return new Win32.RECT(x, y, x + w, y + h);
        throw new CliException("--rect must be x,y,width,height in screen pixels, for example 0,0,800,600.");
    }

    /// <summary>A window from a handle (decimal or 0x hex) or from its title: an exact title
    /// first, then the topmost window whose title contains the text.</summary>
    private static IntPtr ResolveWindow(string spec)
    {
        if (spec.Equals("active", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
        bool isHandle = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(spec[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handle)
            : long.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out handle);
        if (isHandle)
        {
            var h = new IntPtr(handle);
            if (!Win32.IsWindow(h)) throw new CliException($"No window has the handle {spec}.", ExitFailed);
            var root = WindowCapture.CapturableRoot(h);
            if (root == IntPtr.Zero) throw new CliException($"Window {spec} is minimized, hidden or cannot be captured.", ExitFailed);
            return root;
        }
        var windows = FrozenDesktop.EnumerateWindows();
        foreach (var w in windows)
            if (Win32.WindowTitle(w.Handle).Equals(spec, StringComparison.OrdinalIgnoreCase)) return w.Handle;
        foreach (var w in windows)
            if (Win32.WindowTitle(w.Handle).Contains(spec, StringComparison.OrdinalIgnoreCase)) return w.Handle;
        throw new CliException($"No capturable window title contains '{spec}'. Run killershot windows to list them.", ExitFailed);
    }

    /// <summary>The monitor --monitor names, or null for the one under the pointer.</summary>
    private static MonitorBounds? ResolveMonitor(string? spec)
    {
        if (spec == null || spec.Equals("pointer", StringComparison.OrdinalIgnoreCase)) return null;
        var list = OrderedMonitors();
        if (spec.Equals("primary", StringComparison.OrdinalIgnoreCase)) return list.FirstOrDefault(m => m.Primary) ?? list[0];
        if (int.TryParse(spec, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= 1 && n <= list.Count) return list[n - 1];
        throw new CliException($"--monitor must be primary, pointer or a number from 1 to {list.Count}.");
    }

    /// <summary>Monitors numbered left to right, then top to bottom, so the numbers stay put
    /// between runs.</summary>
    private static List<MonitorBounds> OrderedMonitors() =>
        FrozenDesktop.EnumerateMonitors().OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top).ToList();

    private static int MonitorNumber(MonitorBounds monitor) =>
        OrderedMonitors().FindIndex(m => m.Handle == monitor.Handle) + 1;

    private static Win32.RECT VirtualDesktop()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN), y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        return new Win32.RECT(x, y, x + Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN), y + Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));
    }

    // ------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>Opens an image in the editor: a running KillerShot picks it up through the wake
    /// event, otherwise KillerShot starts with it open.</summary>
    private static void OpenInEditor(string path)
    {
        string exe = Environment.ProcessPath is { } self && Path.GetFileName(self).Equals(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase)
            ? self
            : Path.Combine(AppContext.BaseDirectory, AppInfo.ExeName);
        if (!File.Exists(exe)) throw new CliException("KillerShot.exe was not found beside the command line tool.", ExitFailed);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add("--open");
        start.ArgumentList.Add(path);
        using var _ = Process.Start(start);
    }

    private static bool CopyFileDrop(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { path });
                return true;
            }
            catch { Thread.Sleep(50); }
        }
        return false;
    }

    private static (int Width, int Height) ImageSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            return (decoder.Frames[0].PixelWidth, decoder.Frames[0].PixelHeight);
        }
        catch { return (0, 0); }
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    private static string Handle(IntPtr hwnd) => "0x" + hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    private static void WriteBounds(Utf8JsonWriter j, Win32.RECT b)
    {
        j.WriteNumber("left", b.Left);
        j.WriteNumber("top", b.Top);
        j.WriteNumber("width", b.Width);
        j.WriteNumber("height", b.Height);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Connects stdout and stderr. A redirected handle is kept as it is, because
    /// attaching first would swap a pipe for the console and lose the output. Started from a
    /// console without redirection, the GUI build attaches to that console so the text shows.</summary>
    private static void OpenConsole(bool attach)
    {
        try
        {
            if (attach && !IsRedirected(Win32.STD_OUTPUT_HANDLE) && !IsRedirected(Win32.STD_ERROR_HANDLE))
                Win32.AttachConsole(Win32.ATTACH_PARENT_PROCESS);
            var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var error = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(error);
            Out = output;
            Err = error;
        }
        catch
        {
            Out = TextWriter.Null;
            Err = TextWriter.Null;
        }
    }

    private static bool IsRedirected(int which)
    {
        IntPtr handle = Win32.GetStdHandle(which);
        return handle != IntPtr.Zero && handle != new IntPtr(-1) && Win32.GetFileType(handle) != Win32.FILE_TYPE_CHAR;
    }

    private static void PrintUsage(TextWriter w) => w.Write(Usage);

    private const string Usage = """
        KillerShot command line

        Usage:
          killershot <command> [options]
          .\killershot <command> [options]   Portable copy, from its folder.
          KillerShot.exe --cli <command> [options]
          KillerShot.Cli.exe <command> [options]

        Commands:
          capture [options]               Take a screenshot and print where it was saved.
          qr <image|region> [--json]       Read QR codes locally without opening their links.
          record --seconds <n> [options]  Record the screen to MP4 or GIF for a set time.
          recent [options]                List, copy, export or open captures in the library.
          open <image>                    Open an image in the KillerShot editor.
          monitors                        List monitors with their numbers and bounds.
          windows [--limit <n>]           List capturable windows with their handles and titles.
          settings                        Show the capture and recording settings in use.
          version                         Print the KillerShot version.
          help                            Show this help.

        capture options:
          --mode region|window|monitor|all  What to capture. Default: the last mode used.
          --rect <x,y,width,height>       Capture this rectangle without the selector.
          --monitor <n|primary|pointer>   Monitor number from the monitors command.
                                          Default: the monitor under the pointer.
          --window <handle|title>         Handle from the windows command, or part of a title.
                                          Default: the window in front.
          --pick                          Click the window to capture.
          --out <file|folder>             Save to this .png, .jpg or .jpeg file, or into this folder.
          --save, --no-save               Save to the output folder from settings.
          --format png|jpg                Image format when saving into a folder.
          --quality <10-100>              JPEG quality.
          --clipboard, --no-clipboard     Copy the capture to the clipboard.
          --delay <0-30>                  Seconds to wait before capturing.
          --cursor, --no-cursor           Include the mouse pointer.
          --shutter, --no-shutter         Play the shutter sound.
          --editor                        Open the capture in the editor.
          --no-recent                     Leave the capture out of the library.
          --overwrite                     Replace an existing --out file.
          --json                          Print the result as JSON.

        record options:
          --seconds <1-3600>              How long to record. Required. Ctrl+C stops early.
          --mode region|window|monitor|all  What to record. Default: region, chosen on screen.
          --rect, --monitor, --window, --pick  The same as for capture.
          --out <file|folder>             Save to this .mp4 or .gif file, or into this folder.
          --format mp4|gif                Default: the setting, or the --out file's extension.
          --fps <1-60>                    Frames per second.
          --quality small|balanced|high   MP4 quality.
          --sound none|system|mic|both    Sound for MP4 recordings.
          --gif-colors <2-256>            GIF palette size.
          --gif-size <10-100>             GIF size as a percentage of the recorded area.
          --loop, --no-loop               Loop the GIF.
          --cursor, --no-cursor           Include the mouse pointer.
          --clipboard                     Copy the finished file to the clipboard.
          --overwrite, --json             The same as for capture.

        recent options:
          --limit <1-1000>                How many captures to list. Default: 20.
          --search <words>                Only captures whose title, program, note,
                                          collection or text in the picture has every word.
          --copy <n>                      Copy capture n to the clipboard. 1 is the newest.
          --export <n> --out <file|folder>  Save a copy of capture n.
          --open <n>                      Open capture n in the editor.
          --json                          Print the result as JSON.

        Options left out follow your KillerShot settings.
        Exit codes: 0 success, 1 failure, 2 invalid command line, 3 cancelled.

        """;

    private sealed class CliException(string message, int code = ExitUsage) : Exception(message)
    {
        public int Code { get; } = code;
    }

    /// <summary>--name value options, --name switches and positional arguments. An unknown or
    /// repeated option is an error rather than something silently ignored.</summary>
    private sealed class Options
    {
        private readonly Dictionary<string, string?> _items = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positionals { get; } = [];

        public static Options Parse(string[] args, string[] values, string[] flags)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--", StringComparison.Ordinal) || a.Length == 2)
                {
                    o.Positionals.Add(a);
                    continue;
                }
                string name = a[2..];
                string? inline = null;
                int eq = name.IndexOf('=');
                if (eq > 0)
                {
                    inline = name[(eq + 1)..];
                    name = name[..eq];
                }
                name = name.ToLowerInvariant();
                if (o._items.ContainsKey(name)) throw new CliException($"--{name} was given more than once.");
                if (Array.IndexOf(values, name) >= 0)
                {
                    string? value = inline ?? (i + 1 < args.Length ? args[++i] : null);
                    if (string.IsNullOrWhiteSpace(value)) throw new CliException($"--{name} needs a value.");
                    o._items[name] = value;
                }
                else if (Array.IndexOf(flags, name) >= 0 && inline == null)
                {
                    o._items[name] = null;
                }
                else
                {
                    throw new CliException($"Unknown option --{name}.");
                }
            }
            return o;
        }

        public bool Has(string name) => _items.ContainsKey(name);

        public string? Value(string name) => _items.TryGetValue(name, out var v) ? v : null;

        public bool? Switch(string on, string off)
        {
            bool yes = Has(on), no = Has(off);
            if (yes && no) throw new CliException($"--{on} and --{off} cannot be combined.");
            return yes ? true : no ? false : null;
        }

        public int? Int(string name, int min, int max)
        {
            if (Value(name) is not { } text) return null;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                throw new CliException($"--{name} must be a whole number from {min} to {max}.");
            return n;
        }

        public void NoPositionals()
        {
            if (Positionals.Count > 0) throw new CliException($"Unexpected argument '{Positionals[0]}'.");
        }
    }
}
