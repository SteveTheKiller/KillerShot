using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Native;
using KillerShot.Services;
using KillerShot.Video;

namespace KillerShot.Shell;

/// <summary>Shell, settings, update and locale review checks: one PASS or FAIL line per check, so
/// a single failure never hides the rest.</summary>
internal static class SelfTestShellReview
{
    private static readonly string[] LocaleFiles =
        ["bn", "cs-CZ", "de-DE", "en-US", "es", "fr-FR", "hu-HU", "it-IT", "ja-JP", "kk-KZ", "nb-NO", "pl-PL",
         "pt-BR", "ru-RU", "sl-SI", "tr-TR", "uk-UA", "vi-VN", "zh-CN", "zh-TW"];

    public static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-shell-review") return false;
        string dir = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(dir);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        App.InitializeKit();
        LocaleManager.Apply(Locale.EnUS);
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var report = new List<string>();
        bool failed = false;
        async Task Check(string name, Func<Task> body)
        {
            try
            {
                await body().WaitAsync(TimeSpan.FromSeconds(20));
                report.Add("PASS " + name);
            }
            catch (Exception ex)
            {
                failed = true;
                report.Add("FAIL " + name + ": " + (ex is TargetInvocationException { InnerException: { } inner } ? inner : ex).Message);
            }
            File.WriteAllLines(Path.Combine(dir, "shell-review-report.txt"), report);
        }
        Task Sync(Action body) { body(); return Task.CompletedTask; }

        MainWindow? window = null;
        try
        {
            await Check("SH-001 recording Save As name has no invalid file name characters", () => Sync(CheckVideoSaveName));
            await Check("SH-004 deleting a folder file that is gone reports it without asking", () => Sync(CheckMissingFolderDelete));
            await Check("SH-003 deleting the open folder picture closes a clean editor and keeps a dirty one", () => CheckOpenFolderDelete(dir));
            await Check("SH-005/006/007/010/011 listing and grouping keep their results (refactor guard)", () => CheckListingGuard(dir));
            await Check("SH-012 tray Open folder reports a failure", () => CheckTrayOpenFolder(dir));
            await Check("SR-001 a cancelled exit does not suppress the next recording's question", () => CheckExitReset(dir));
            await Check("UI-001 every accent has a distinct name in every language", () => Sync(CheckAccentNames));
            await Check("UI-003/008 new strings exist in all 20 languages and the FPS names differ", () => Sync(CheckNewStrings));
            await Check("UI-006/007 AboutCloseFontSize and TabPlacementTransform resolve in Dark and 98SE", () => Sync(() => CheckThemeKeys(dir)));
            await Check("UP-001 the update check returns the release tag as published", () => Sync(CheckReleaseTag));
            await Check("UP-005 a failed re-read of the translation file keeps the last good one", () => Sync(() => CheckExternalLocale(dir)));

            ThemeManager.Apply(Theme.Dark);
            window = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Show();
            window.Left = -20000;
            window.Top = -20000;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var w = window;
            await Check("UI-002/003/004/005 settings fields have accessible names", () => Sync(() => CheckSettingsNames(w)));
            await Check("SH-009 a workspace menu fills once per open", () => CheckMenuFills(w));
            await Check("SR-003 opening Shortcuts during a fade keeps the frame dimmed", () => CheckShortcutsDim(w));
            await Check("SR-006 keyboard map keys are focusable, named and show their detail on focus", () => CheckKeycaps(w));
        }
        catch (Exception ex) { failed = true; report.Add("FAIL harness: " + ex); }
        finally
        {
            File.WriteAllLines(Path.Combine(dir, "shell-review-report.txt"), report);
            foreach (Window open in Application.Current.Windows.Cast<Window>().ToArray()) { try { open.Close(); } catch { } }
            App.Instance.Shutdown(failed ? 1 : 0);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ---- SH-001 ----

    private static void CheckVideoSaveName()
    {
        var method = typeof(LibraryView).GetMethod("VideoSaveName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("LibraryView.VideoSaveName is missing.");
        string Name(string? window) => (string)method.Invoke(null, [window, ".mp4"])!;
        var invalid = Path.GetInvalidFileNameChars();
        string a = Name("a/b:c?*\"<>|");
        Require(a.EndsWith(".mp4", StringComparison.Ordinal) && a[..^4].IndexOfAny(invalid) < 0, $"'{a}' keeps invalid characters.");
        string fallback = LocaleManager.Loc("Str_Rec_CollectionName");
        Require(Name("  ... ") == fallback + ".mp4", "A title of dots and spaces did not fall back to the recording name.");
        Require(Name(null) == fallback + ".mp4", "No title did not fall back to the recording name.");
        Require(Name("Report.") == "Report.mp4", "A trailing dot stayed in the name.");
        Require(Name("Clock {mm}") == "Clock {mm}.mp4", "A window title was expanded as a file name pattern.");
    }

    // ---- SH-004 / SH-003 ----

    private static void CheckMissingFolderDelete()
    {
        var view = new LibraryView();
        int asked = 0;
        var messages = new List<string>();
        view.PresentConfirmation = dialog => { asked++; dialog.Close(); return false; };
        view.StatusMessage += messages.Add;
        view.Delete(new CaptureItem(-987654321, DateTime.Now, 2, 2, "region", "gone", null, FolderCaptures.CollectionId, null));
        Require(asked == 0, "A missing file still asked to be deleted for good.");
        Require(messages.SequenceEqual([LocaleManager.Loc("Str_Lib_Missing")]), $"Status was [{string.Join(" | ", messages)}].");
    }

    private static void WritePng(string path)
    {
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task CheckOpenFolderDelete(string dir)
    {
        string folder = Path.Combine(dir, "screenshots-delete");
        Directory.CreateDirectory(folder);
        string clean = Path.Combine(folder, "clean.png"), dirty = Path.Combine(folder, "dirty.png");
        WritePng(clean);
        WritePng(dirty);
        FolderCaptures.UseFolder(folder);
        var items = FolderCaptures.Items();
        var view = new LibraryView();
        var seam = typeof(LibraryView).GetProperty("RecycleFolderFile", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("LibraryView.RecycleFolderFile is missing.");
        // The test deletes its own scratch file instead of filling the Recycle Bin.
        seam.SetValue(view, new Action<long, IntPtr>((id, _) => File.Delete(FolderCaptures.PathOf(id)!)));
        view.PresentConfirmation = dialog => { dialog.Close(); return true; };
        App.Instance.PresentDocumentReplacement = dialog => { dialog.Close(); return true; };
        try
        {
            var cleanItem = items.Single(i => FolderCaptures.PathOf(i.Id) == clean);
            Require(App.Instance.OpenLibraryCapture(cleanItem) && App.Instance.CurrentItem?.Id == cleanItem.Id, "The clean folder picture did not open.");
            view.Delete(cleanItem);
            await Task.Delay(100);
            Require(!File.Exists(clean), "The clean folder picture was not deleted.");
            Require(App.Instance.CurrentItem == null && App.Instance.Document == null, "The editor kept a deleted, unchanged folder picture open.");

            var dirtyItem = items.Single(i => FolderCaptures.PathOf(i.Id) == dirty);
            Require(App.Instance.OpenLibraryCapture(dirtyItem), "The dirty folder picture did not open.");
            var doc = App.Instance.Document!;
            doc.Apply(doc.State with { });
            view.Delete(dirtyItem);
            await Task.Delay(100);
            Require(!File.Exists(dirty), "The dirty folder picture was not deleted.");
            Require(ReferenceEquals(App.Instance.Document, doc) && doc.Dirty, "Deleting the file threw away unsaved edits.");
        }
        finally
        {
            App.Instance.PresentDocumentReplacement = dialog => { dialog.Close(); return true; };
            App.Instance.CompleteAlbumSwitch();
            App.Instance.PresentDocumentReplacement = null;
        }
    }

    // ---- SH-005/006/007/010/011 guard ----

    private static async Task CheckListingGuard(string dir)
    {
        string folder = Path.Combine(dir, "screenshots-listing");
        Directory.CreateDirectory(folder);
        WritePng(Path.Combine(folder, "one.png"));
        FolderCaptures.UseFolder(folder);
        FolderCaptures.Items();
        await FolderCaptures.RefreshScreenshotsAsync();
        var all = LibraryListing.Read("all", "");
        Require(all.FolderItems.Count == 1, $"All listed {all.FolderItems.Count} folder pictures.");
        var only = LibraryListing.Read("folder", "");
        Require(only.Items.Count == 1 && only.FolderItems.Count == 0 && only.Collections.Count == 0, "The folder listing changed shape.");
        var searched = LibraryListing.Read("folder", "nomatch");
        Require(searched.Items.Count == 0 && searched.Total == 1, "Searching the folder changed its counts.");
    }

    // ---- SH-012 ----

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static Task CheckTrayOpenFolder(string dir)
    {
        string blocker = Path.Combine(dir, "folder-is-a-file");
        File.WriteAllText(blocker, "");
        string before = CaptureSettings.OutputFolder;
        CaptureSettings.OutputFolder = Path.Combine(blocker, "sub");
        using var tray = new TrayHost();
        var warnings = new List<string>();
        var shown = typeof(TrayHost).GetEvent("WarningShown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("TrayHost.WarningShown is missing.");
        shown.GetAddMethod(true)!.Invoke(tray, [new Action<string, string>((_, text) => warnings.Add(text))]);
        try
        {
            SendMessage(tray.Handle, 0x8001, IntPtr.Zero, (IntPtr)0x7B);
            var menu = PresentationSource.CurrentSources.Cast<PresentationSource>().Where(s => s.RootVisual != null)
                .SelectMany(s => Visuals(s.RootVisual)).OfType<ContextMenu>().Single(m => m.IsOpen);
            try
            {
                var item = menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, LocaleManager.Loc("Str_Tray_OpenFolder")));
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            finally { menu.IsOpen = false; }
            Require(warnings.Count == 1 && warnings[0].Length > 0, $"Open folder raised {warnings.Count} warnings.");
        }
        finally { CaptureSettings.OutputFolder = before; }
        return Task.CompletedTask;
    }

    // ---- SR-001 ----

    private sealed class Frames : IFrameGrabber
    {
        public int Width => 64;
        public int Height => 64;
        public bool Grab(byte[] bgra) { Array.Fill(bgra, (byte)255); return true; }
        public void Dispose() { }
    }

    private static async Task CheckExitReset(string dir)
    {
        VideoSettings.OutputFolder = Path.Combine(dir, "videos");
        VideoSettings.Format = "gif";
        VideoSettings.Sound = SoundSource.None;
        VideoSettings.Countdown = VideoSettings.CopyFile = VideoSettings.Notify = false;
        RecordingStorage.Choice = null;
        using var tray = new TrayHost();
        var controller = new VideoController(tray, grabberFactory: () => new Frames(), showWarning: (_, _) => { });
        int asked = 0;
        controller.AskStorage = _ => { asked++; return (null, false); };
        try
        {
            await controller.StartAsync(new Win32.RECT(0, 0, 64, 64));
            await Task.Delay(300);
            Require(await controller.PrepareForExitAsync(), "The exit stop did not save the clip.");
            Require(asked == 0, "Quitting asked the first-recording question.");
            // The exit was cancelled (an unsaved editor said no), so the app keeps running.
            await controller.StartAsync(new Win32.RECT(0, 0, 64, 64));
            await Task.Delay(300);
            await controller.StopAsync(false);
            Require(asked == 1, $"The next recording asked the question {asked} times.");
        }
        finally { await controller.StopAsync(true); }
    }

    // ---- UI-001 / strings ----

    private static void CheckAccentNames()
    {
        foreach (Locale locale in Enum.GetValues<Locale>())
        {
            LocaleManager.Apply(locale);
            var names = Enum.GetValues<Accent>().Select(a => Application.Current.TryFindResource("Str_Color_" + a) as string).ToArray();
            Require(names.All(n => !string.IsNullOrWhiteSpace(n)), $"{locale}: an accent has no name.");
            Require(names.Distinct().Count() == names.Length, $"{locale}: accent names repeat [{string.Join(", ", names)}].");
        }
        LocaleManager.Apply(Locale.EnUS);
    }

    private static void CheckNewStrings()
    {
        var missing = new List<string>();
        foreach (string file in LocaleFiles)
        {
            var rd = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Strings/{file}.xaml") };
            foreach (string key in new[] { "Str_Color_Magenta", "Str_VS_Mp4Fps", "Str_VS_GifFps", "Str_Status_ShellMixedFolders" })
                if (rd[key] is not string { Length: > 0 }) missing.Add(file + ":" + key);
            if (rd["Str_VS_Mp4Fps"] is string mp4 && Equals(mp4, rd["Str_VS_GifFps"])) missing.Add(file + ": MP4 and GIF frame rate share a name");
        }
        Require(missing.Count == 0, string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? $" (+{missing.Count - 12})" : ""));
    }

    // ---- UI-006 / UI-007 ----

    private static void CheckThemeKeys(string dir)
    {
        var problems = new List<string>();
        foreach (Theme theme in new[] { Theme.Dark, Theme.SE98 })
        {
            ThemeManager.Apply(theme);
            if (Application.Current.TryFindResource("AboutCloseFontSize") is not double size || size <= 0) problems.Add(theme + " AboutCloseFontSize");
            if (Application.Current.TryFindResource("TabPlacementTransform") is not Transform) problems.Add(theme + " TabPlacementTransform");
            RenderClose(theme, Path.Combine(dir, "close-" + theme + ".png"));
        }
        ThemeManager.Apply(Theme.Dark);
        Require(problems.Count == 0, "Unresolved: " + string.Join(", ", problems));
    }

    private static void RenderClose(Theme theme, string path)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("OverlayCloseButton") };
        button.SetResourceReference(ContentControl.ContentProperty, "AboutCloseGlyph");
        var host = new Border { Child = button, Padding = new Thickness(6) };
        host.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        var bmp = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(host.ActualWidth * 4)), Math.Max(1, (int)Math.Ceiling(host.ActualHeight * 4)), 384, 384, PixelFormats.Pbgra32);
        bmp.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ---- UP-001 ----

    private static void CheckReleaseTag()
    {
        var current = new Version(1, 0, 0);
        Require(UpdateService.NewerReleaseTag("{\"tag_name\":\"9.9.9\",\"draft\":false,\"prerelease\":false}", current) == "9.9.9",
            "A tag without a v came back rebuilt.");
        Require(UpdateService.NewerReleaseTag("{\"tag_name\":\"v9.9.9\",\"draft\":false,\"prerelease\":false}", current) == "v9.9.9",
            "A v tag did not come back as published.");
        Require(UpdateService.NewerReleaseTag("{\"tag_name\":\"v0.9.0\",\"draft\":false,\"prerelease\":false}", current) == null,
            "An older tag was offered.");
    }

    // ---- UP-005 ----

    private static void CheckExternalLocale(string dir)
    {
        string file = Path.Combine(dir, "external-strings.xaml");
        File.WriteAllText(file,
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">" +
            "<sys:String x:Key=\"Str_Lib_Missing\">EXTERNAL MISSING</sys:String></ResourceDictionary>");
        LocaleManager.ExternalFile = file;
        try
        {
            LocaleManager.Apply(Locale.EnUS);
            Require(LocaleManager.Loc("Str_Lib_Missing") == "EXTERNAL MISSING", "The translation file did not load the first time.");
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                LocaleManager.Apply(Locale.De);
            Require(LocaleManager.Loc("Str_Lib_Missing") == "EXTERNAL MISSING",
                $"A failed re-read replaced the translator's file with the built-in language ('{LocaleManager.Loc("Str_Lib_Missing")}').");
        }
        finally
        {
            LocaleManager.ExternalFile = null;
            LocaleManager.Apply(Locale.EnUS);
        }
    }

    // ---- Window checks ----

    private static void CheckSettingsNames(MainWindow window)
    {
        string[] names = ["FormatCombo", "QualityCombo", "DelayCombo", "OutputFolderBox", "FileNamePatternBox",
            "VideoFormatCombo", "Mp4FpsCombo", "Mp4QualityCombo", "SoundCombo", "AudioKbpsCombo",
            "GifFpsCombo", "GifColorsCombo", "GifSizeCombo", "VideoFolderBox", "VideoFileNameBox"];
        var unnamed = new List<string>();
        var spoken = new Dictionary<string, string>();
        foreach (string name in names)
        {
            var element = (UIElement)(window.FindName(name) ?? throw new InvalidOperationException(name + " is missing."));
            string text = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName() ?? "";
            if (string.IsNullOrWhiteSpace(text)) unnamed.Add(name);
            spoken[name] = text;
        }
        Require(unnamed.Count == 0, "Unnamed: " + string.Join(", ", unnamed));
        Require(spoken["Mp4FpsCombo"] != spoken["GifFpsCombo"], "The MP4 and GIF frame rate boxes share a name.");
    }

    private static async Task CheckMenuFills(MainWindow window)
    {
        var rail = window.RailStack;
        var menu = rail.ContextMenu ?? throw new InvalidOperationException("The sidebar has no menu.");
        int fills = 0, opened = 0;
        menu.Opened += (_, _) => opened++;
        // Clearing an empty menu raises nothing, so every fill below starts from a full one.
        menu.Items.Add(new Separator());
        ((System.Collections.Specialized.INotifyCollectionChanged)menu.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) fills++;
        };
        var ctor = typeof(ContextMenuEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).First();
        object?[] values = ctor.GetParameters().Select(p => p.ParameterType == typeof(bool) ? (object)true
            : p.ParameterType == typeof(object) ? rail : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var args = (ContextMenuEventArgs)ctor.Invoke(values);
        args.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
        rail.RaiseEvent(args);
        Require(!args.Handled, $"The sidebar menu refused to open ({fills} fills).");
        menu.PlacementTarget = rail;
        menu.IsOpen = true;
        await Task.Delay(300);
        int pointer = fills, pointerOpened = opened;
        menu.IsOpen = false;
        await Task.Delay(100);
        fills = 0;
        menu.IsOpen = true;
        await Task.Delay(300);
        int direct = fills;
        menu.IsOpen = false;
        Require(pointerOpened == 1, $"Opened fired {pointerOpened} times.");
        Require(pointer == 1 && direct == 1, $"Fills per open: right-click {pointer}, direct open {direct}.");
    }

    private static async Task CheckShortcutsDim(MainWindow window)
    {
        window.TestCloseOverlays();
        window.TestShowCaptureSettings();
        await Task.Delay(300);
        var fadeOut = typeof(MainWindow).GetMethod("FadeOverlayOut", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fadeOut.Invoke(window, [window.CaptureSettingsOverlay]);
        await Task.Delay(40);
        window.TestShowShortcuts(false);
        await Task.Delay(500);
        double opacity = window.ClassicFrameChrome.Opacity;
        window.TestCloseOverlays();
        Require(Math.Abs(opacity - 0.42) < 0.001, $"The frame behind Shortcuts came back to {opacity}.");
    }

    private static async Task CheckKeycaps(MainWindow window)
    {
        window.TestCloseOverlays();
        window.TestShowShortcuts(true);
        window.UpdateLayout();
        await Task.Delay(200);
        var detail = (TextBlock?)typeof(MainWindow).GetField("_kbDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)
            ?? throw new InvalidOperationException("The keyboard map has no detail line.");
        var keys = Visuals(window.ShortcutsOverlay).OfType<Border>().Where(b => b.Focusable && b.Height == 44).ToArray();
        Require(keys.Length > 5, $"Only {keys.Length} keycaps take keyboard focus.");
        foreach (var key in keys)
        {
            string name = UIElementAutomationPeer.CreatePeerForElement(key)?.GetName() ?? "";
            Require(name.Trim().Length > 0, "A focusable keycap has no automation name.");
            key.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, null, key) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Require(detail.Text == name, $"Focus showed '{detail.Text}' for a key named '{name}'.");
            key.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, key, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
        }
        window.TestCloseOverlays();
    }
}
