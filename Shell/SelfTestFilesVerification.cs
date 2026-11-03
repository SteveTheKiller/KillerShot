using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestFilesVerification
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--selftest-files-verification") return false;
        _ = Run(Path.GetFullPath(args[1]), args.Length == 3 ? args[2] : "network");
        return true;
    }

    private static async Task Run(string folder, string scenario)
    {
        int checks = 0, exit = 1;
        var report = new StringBuilder();
        void Check(bool passed, string description)
        {
            checks++;
            report.AppendLine((passed ? "PASS: " : "FAIL: ") + description);
            if (!passed) throw new InvalidOperationException(description);
        }
        try
        {
            if (Directory.Exists(folder)) throw new IOException("Use a fresh verification folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            FolderCaptures.UseFolder(Path.Combine(folder, "screenshots"));
            FolderCaptures.Enabled = false;
            VideoCaptures.UsePlaybackFolder(Path.Combine(folder, "playback"));
            Settings.Set("Files.Places", "");
            App.InitializeKit();
            App.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (scenario.StartsWith("render", StringComparison.Ordinal))
            {
                await RenderSuite(folder, scenario, Check, report);
                exit = 0;
                return;
            }
            using var files = new FilesView();
            string owned = Directory.CreateDirectory(Path.Combine(folder, "owned")).FullName;
            Check(await files.NavigateTo(owned), "public navigation opens owned fixture");
            string document = Path.Combine(owned, "typed.txt");
            File.WriteAllText(document, "owned activation");
            string? activated = null;
            files.Browser.OpenInEditor = path => { activated = path; return Task.FromResult(true); };
            Check(!await files.NavigateAddress(document + Path.DirectorySeparatorChar) && activated == null,
                "typed file with trailing separator retains directory-only semantics");
            string address = @"\\192.0.2.1\ks-files-e-" + Guid.NewGuid().ToString("N") + @"\unreachable.txt";
            if (scenario == "network2") address = address.Replace("192.0.2.1", "198.51.100.1", StringComparison.Ordinal);
            report.AppendLine("Test-only unreachable address: " + address);
            async Task<(double Return, double Total, double Max, int Pings, bool Result)> Probe(Func<Task<bool>> navigate)
            {
                long maximum = 0;
                int pings = 0;
                using var stop = new CancellationTokenSource();
                var dispatcher = Dispatcher.CurrentDispatcher;
                var thread = new Thread(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        long posted = Stopwatch.GetTimestamp();
                        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                        {
                            maximum = Math.Max(maximum, Stopwatch.GetTimestamp() - posted);
                            pings++;
                        });
                        Thread.Sleep(20);
                    }
                }) { IsBackground = true };
                thread.Start();
                await Task.Delay(60);
                var clock = Stopwatch.StartNew();
                var pending = navigate();
                double synchronous = clock.Elapsed.TotalMilliseconds;
                bool result = await pending;
                stop.Cancel();
                thread.Join();
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                return (synchronous, clock.Elapsed.TotalMilliseconds, maximum * 1000d / Stopwatch.Frequency, pings, result);
            }
            var direct = await Probe(() => files.NavigateTo(address));
            report.AppendLine($"Direct: return={direct.Return:F1}ms total={direct.Total:F1}ms max={direct.Max:F1}ms pings={direct.Pings} result={direct.Result}");
            Check(!direct.Result && direct.Pings > 0 && direct.Max < 500, "direct unreachable navigation remains responsive");
            var typed = await Probe(() => files.NavigateAddress(address));
            report.AppendLine($"Address: return={typed.Return:F1}ms total={typed.Total:F1}ms max={typed.Max:F1}ms pings={typed.Pings} result={typed.Result}");
            Check(!typed.Result && typed.Pings > 0 && typed.Return < 500 && typed.Max < 500 && typed.Total < 6000,
                "public unreachable address returns promptly, keeps dispatcher responsive and completes within deadline");
            var obsolete = files.NavigateAddress(address);
            Check(await files.NavigateTo(owned), "later owned navigation succeeds while address probe is pending");
            Check(!await obsolete && files.Navigation.CurrentFolder == owned && files.Navigation.Error == null,
                "superseded address cannot publish folder or error over later navigation");
            Check(await files.NavigateAddress('"' + document + '"') && activated == document,
                "quoted typed file retains handled host activation");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, document);
            activated = null;
            Check(await files.NavigateAddress(relative) && activated == document, "relative typed file resolves before host activation");
            Check(await files.NavigateAddress(owned) && files.Navigation.CurrentFolder == owned, "typed directory retains browsing");
            activated = null;
            var pendingAddress = files.NavigateAddress(address);
            files.Dispose();
            Check(!await pendingAddress && activated == null, "disposal cancels pending address before any host activation");
            await Task.Delay(18000);
            Check(activated == null && files.Navigation.CurrentFolder == owned,
                "late network probe completion after disposal cannot activate host or replace folder");
            exit = 0;
        }
        catch (Exception error) { report.AppendLine(error.ToString()); }
        finally
        {
            report.AppendLine($"Assertions: {checks}; Exit: {exit}");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "files-verification-report.txt"), report.ToString());
            Application.Current.Shutdown(exit);
        }
    }

    private sealed class TestKeyboard(ModifierKeys modifiers) : KeyboardDevice(InputManager.Current)
    {
        protected override KeyStates GetKeyStatesFromSystem(Key key) => key switch
        {
            Key.LeftCtrl when modifiers.HasFlag(ModifierKeys.Control) => KeyStates.Down,
            Key.LeftShift when modifiers.HasFlag(ModifierKeys.Shift) => KeyStates.Down,
            Key.LeftAlt when modifiers.HasFlag(ModifierKeys.Alt) => KeyStates.Down,
            _ => KeyStates.None
        };
    }

    private static bool SendKey(MainWindow window, UIElement target, Key key, ModifierKeys modifiers = ModifierKeys.None)
    {
        var input = new KeyEventArgs(new TestKeyboard(modifiers), PresentationSource.FromVisual(window)!, 0, key);
        input.RoutedEvent = Keyboard.PreviewKeyDownEvent;
        target.RaiseEvent(input);
        if (!input.Handled)
        {
            input.RoutedEvent = Keyboard.KeyDownEvent;
            target.RaiseEvent(input);
        }
        return input.Handled;
    }

    private static IEnumerable<T> Children<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }

    private static BitmapSource Render(FrameworkElement element)
    {
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(element.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(element.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null,
                new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(element)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static int Ink(BitmapSource image)
    {
        byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        int count = 0;
        for (int index = 3; index < pixels.Length; index += 4) if (pixels[index] > 16) count++;
        return count;
    }

    private static async Task Idle() => await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static async Task RenderSuite(string folder, string scenario, Action<bool, string> check, StringBuilder report)
    {
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
        CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
        CaptureLibrary.BackgroundWork = false;
        string owned = Directory.CreateDirectory(Path.Combine(folder, "files")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(owned, "Holiday photos")).FullName;
        File.WriteAllText(Path.Combine(owned, "Camera notes.txt"), "Owned verification fixture.");
        File.WriteAllText(Path.Combine(owned, "Landscape.png"), "Not activated by this render test.");
        CaptureSettings.OutputFolder = owned;
        VideoSettings.OutputFolder = Directory.CreateDirectory(Path.Combine(folder, "recordings")).FullName;
        FolderCaptures.RecordingsFolderOverride = VideoSettings.OutputFolder;
        LocaleManager.Apply(Locale.EnUS);
        check(CaptureLibrary.OpenAuto(), "owned verification album opens");
        var window = new MainWindow
        {
            Width = 1600, Height = 1000, Left = -20000, Top = -20000,
            ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            window.Show();
            await Idle();
            var library = (LibraryView)window.FindName("Library");
            check(SendKey(window, library, Key.F9), "routed F9 is handled by actual MainWindow");
            await library.FilesNavigation;
            var files = library.Files;
            await files.NavigateTo(owned);
            files.SetDensity(1);
            check(library.FilesActive && files.IsVisible, "integrated Files surface is active");
            if (scenario == "render-keys")
            {
                files.ResultsList.Focus();
                files.BeginEditAddress();
                report.AppendLine("Alt+D requires native system-key delivery and remains a physical keyboard gate.");
                var address = (TextBox)files.FindName("AddressBox");
                check(address.IsVisible, "keyboard address route exposes address box");
                address.Text = child;
                check(SendKey(window, address, Key.Enter), "routed address Enter is handled");
                await Wait(() => files.Navigation.CurrentFolder == child);
                check(files.Navigation.CurrentFolder == child, "keyboard Enter navigates actual owned folder");
                files.ResultsList.Focus();
                check(SendKey(window, files.ResultsList, Key.Back), "routed Backspace requests parent navigation");
                await Wait(() => files.Navigation.CurrentFolder == owned);
                check(files.Navigation.CurrentFolder == owned, "keyboard Backspace reaches actual parent");
                int beforeMode = files.CurrentMode;
                check(SendKey(window, files.ResultsList, Key.D2, ModifierKeys.Control | ModifierKeys.Shift)
                    && Settings.Get("ToolbarIconSize") == "Large" && files.CurrentMode == beforeMode,
                    "integrated Ctrl+Shift+2 retains global large-toolbar ownership");
                check(SendKey(window, files.ResultsList, Key.D1, ModifierKeys.Control | ModifierKeys.Shift)
                    && Settings.Get("ToolbarIconSize") == "Small" && files.CurrentMode == beforeMode,
                    "integrated Ctrl+Shift+1 retains global small-toolbar ownership");
                string[] buttons = ["ViewListBtn", "ViewIconsBtn", "ViewDetailsBtn"];
                for (int mode = 0; mode < buttons.Length; mode++)
                {
                    var button = (Button)files.FindName(buttons[mode]);
                    check(button.IsTabStop && button.Focusable && button.IsEnabled && button.IsVisible,
                        "mode button " + mode + " is an enabled visible keyboard tab target");
                    SendKey(window, button, Key.Space);
                    button.RaiseEvent(new KeyEventArgs(new TestKeyboard(ModifierKeys.None), PresentationSource.FromVisual(window)!, 0, Key.Space)
                        { RoutedEvent = Keyboard.KeyUpEvent });
                    check(files.CurrentMode == mode, "routed Space invokes actual mode button " + mode);
                }
                var first = files.Navigation.Results.First(entry => entry.FileName == "Camera notes.txt");
                files.ResultsList.SelectedItem = first;
                check(SendKey(window, files.ResultsList, Key.F2), "routed F2 starts inline file rename");
                var rename = (TextBox)files.FindName("RenameBox");
                check(rename.IsVisible && rename.Text == "Camera notes.txt", "keyboard rename owns actual row name");
                rename.Text = "Travel notes.txt";
                check(SendKey(window, rename, Key.Enter), "routed rename Enter is handled");
                await files.CurrentCommand;
                check(File.Exists(Path.Combine(owned, "Travel notes.txt")) && !File.Exists(first.FilePath), "keyboard rename changes real owned file");
                return;
            }

            var states = new List<(Theme Theme, Accent? Accent, double Scale, int Mode)>();
            if (scenario == "render-themes")
            {
                files.SetView(2);
                await Idle();
                window.UpdateLayout();
                var header = (FrameworkElement)files.FindName("DetailsHeader");
                var peers = Children<Button>(header).Select(button => UIElementAutomationPeer.CreatePeerForElement(button)!).ToArray();
                LocaleManager.Apply(Locale.Es);
                await Idle();
                check(peers.Select(peer => peer.GetName()).SequenceEqual(new[] { "nombre", "ubicación", "tamaño", "modificado", "tipo", "creado" }),
                    "existing six column peers update their localized names after Spanish language change");
                LocaleManager.Apply(Locale.EnUS);
                await Idle();
                check(peers.Select(peer => peer.GetName()).SequenceEqual(new[] { "name", "location", "size", "modified", "type", "created" }),
                    "existing six column peers restore English names after language change");
                foreach (Theme theme in Enum.GetValues<Theme>())
                    foreach (int mode in new[] { 0, 1, 2 }) states.Add((theme, null, 1, mode));
            }
            else if (scenario == "render-scales")
            {
                foreach (Theme theme in Enum.GetValues<Theme>())
                    foreach (double scale in new[] { 1.25, 1.5 }) states.Add((theme, null, scale, 2));
            }
            else if (scenario == "render-accents")
            {
                foreach (Theme theme in new[] { Theme.Dark, Theme.Light, Theme.Black, Theme.SE98 })
                    foreach (Accent accent in Enum.GetValues<Accent>()) states.Add((theme, accent, 1.25, 1));
            }
            else if (scenario == "render-demo") states.Add((Theme.Dark, Accent.Teal, 1, 2));
            else throw new ArgumentException("Unknown render scenario.");

            foreach (var state in states)
            {
                ThemeManager.Apply(state.Theme);
                if (state.Accent is Accent accent) ThemeManager.ApplyAccent(state.Theme, accent);
                window.ApplyAppScale(state.Scale);
                files.SetView(state.Mode);
                await Idle();
                window.UpdateLayout();
                string label = $"{state.Theme}-{state.Accent?.ToString() ?? "default"}-{state.Scale:F2}-mode{state.Mode}";
                check(files.ResultsList.Items.Count == 3, label + " displays all three actual entries");
                var row = (ListBoxItem)files.ResultsList.ItemContainerGenerator.ContainerFromIndex(0);
                check(row != null && row.ActualHeight > 0, label + " realizes first row");
                string name = ((FileEntry)row!.DataContext).FileName;
                var peer = UIElementAutomationPeer.CreatePeerForElement(row)!;
                check(peer.GetName() == name && peer.GetAutomationControlType() == AutomationControlType.ListItem,
                    label + " row UIA exposes filename and list-item role");
                var text = Children<TextBlock>(row).First(block => block.Text == name);
                int visible = Ink(Render(text));
                string original = text.Text;
                text.SetCurrentValue(TextBlock.TextProperty, "");
                window.UpdateLayout();
                int absent = Ink(Render(text));
                text.SetCurrentValue(TextBlock.TextProperty, original);
                window.UpdateLayout();
                check(visible > 10 && absent == 0, label + $" row-name ink target positive={visible} absent={absent}");
                files.BeginEditAddress();
                var address = (TextBox)files.FindName("AddressBox");
                var addressPeer = UIElementAutomationPeer.CreatePeerForElement(address)!;
                report.AppendLine(label + " address name=" + addressPeer.GetName());
                check(addressPeer.GetName() == "location" && addressPeer.GetAutomationControlType() == AutomationControlType.Edit
                    && ((IValueProvider)addressPeer.GetPattern(PatternInterface.Value)!).Value == owned,
                    label + " address UIA exposes location name, edit role and actual path value");
                address.RaiseEvent(new KeyEventArgs(new TestKeyboard(ModifierKeys.None), PresentationSource.FromVisual(window)!, 0, Key.Escape)
                    { RoutedEvent = Keyboard.KeyDownEvent });
                var pathLabel = (TextBlock)files.FindName("ScopePathLabel");
                window.UpdateLayout();
                int addressInk = Ink(Render(pathLabel));
                string pathText = pathLabel.Text;
                pathLabel.SetCurrentValue(TextBlock.TextProperty, "");
                window.UpdateLayout();
                int absentAddressInk = Ink(Render(pathLabel));
                pathLabel.SetCurrentValue(TextBlock.TextProperty, pathText);
                window.UpdateLayout();
                check(addressInk > 10 && absentAddressInk == 0, label + $" address-path ink positive={addressInk} absent={absentAddressInk}");
                if (state.Mode == 2)
                {
                    var header = (FrameworkElement)files.FindName("DetailsHeader");
                    var headings = Children<Button>(header).ToArray();
                    string[] expected = ["name", "location", "size", "modified", "type", "created"];
                    string[] actual = headings.Select(button => UIElementAutomationPeer.CreatePeerForElement(button)!.GetName()).ToArray();
                    report.AppendLine(label + " column UIA names=" + string.Join("|", actual));
                    check(actual.SequenceEqual(expected), label + " all six column UIA names match readable labels");
                    foreach (var button in headings)
                    {
                        var heading = Children<TextBlock>(button).First();
                        report.AppendLine(label + " column name=" + UIElementAutomationPeer.CreatePeerForElement(button)!.GetName());
                        if (button.ActualWidth > 1)
                        {
                            int headingInk = Ink(Render(heading));
                            string headingText = heading.Text;
                            heading.SetCurrentValue(TextBlock.TextProperty, "");
                            window.UpdateLayout();
                            int noHeadingInk = Ink(Render(heading));
                            heading.SetCurrentValue(TextBlock.TextProperty, headingText);
                            window.UpdateLayout();
                            check(headingInk > 10 && noHeadingInk == 0,
                                label + $" column ink {headingText} positive={headingInk} absent={noHeadingInk}");
                        }
                    }
                    double columns = files.ViewState.RowIconColumn.Value + files.ViewState.NameWidth.Value + files.ViewState.LocationWidth.Value
                        + files.ViewState.SizeWidth.Value + files.ViewState.ModifiedWidth.Value + files.ViewState.TypeWidth.Value
                        + files.ViewState.CreatedWidth.Value + files.ViewState.RowPad.Left + files.ViewState.RowPad.Right;
                    check(columns <= files.ResultsList.ActualWidth - 16, label + " details columns fit actual viewport");
                }
                Save((FrameworkElement)window.Content, Path.Combine(folder, label + ".png"));
            }
            check(states.Count > 0, "nonzero actual rendered state count=" + states.Count);
            if (scenario == "render-demo")
            {
                ThemeManager.Apply(Theme.Dark);
                ThemeManager.ApplyAccent(Theme.Dark, Accent.Teal);
                check(await files.NavigateTo(@"C:\Windows\Web"), "privacy-safe public OS folder opens read-only");
                files.SetView(2);
                await Idle();
                window.UpdateLayout();
                Save(window, Path.Combine(folder, "integrated-files-dark-teal.png"));
                check(await files.NavigateTo(@"C:\Windows\Web\Screen"), "privacy-safe public OS image folder opens read-only");
                files.SetView(1);
                await Idle();
                window.UpdateLayout();
                var images = Children<Image>(files.ResultsList).Where(image => TileArt.GetPath(image) != null).ToArray();
                await Wait(() => images.Length > 0 && images.All(image => image.Source != null));
                check(images.Length > 0 && images.All(image => image.Source != null), "all actual public OS image tiles finish asynchronous art loading");
                window.UpdateLayout();
                foreach (var image in images)
                {
                    int visible = Ink(Render(image));
                    var source = image.Source;
                    image.Source = null;
                    window.UpdateLayout();
                    int absent = Ink(Render(image));
                    image.Source = source;
                    window.UpdateLayout();
                    check(visible > 10 && absent == 0, $"actual OS image tile ink positive={visible} absent={absent}");
                }
                Save(window, Path.Combine(folder, "integrated-files-images-dark-teal.png"));
            }
        }
        finally
        {
            window.Close();
            CaptureLibrary.Close();
        }
    }

    private static async Task Wait(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < TimeSpan.FromSeconds(4)) await Task.Delay(20);
    }
}
