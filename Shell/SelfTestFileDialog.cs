using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShot.Controls;
using KillerShot.Services;
using PickerDialog = KillerShot.Controls.FileDialog;

namespace KillerShot.Shell;

internal static class SelfTestFileDialog
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-file-dialog") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        App.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        var report = new StringBuilder();
        int failures = 0;
        foreach (var (name, check) in new (string, Func<Task>)[]
        {
            ("concurrent folder load", CheckConcurrentLoad),
            ("failed folder load retry", CheckLoadRetry),
            ("hidden toggle during folder load", CheckHiddenRefresh),
            ("responsive listing and current selection", () => CheckNavigation(folder)),
            ("folder picker initial focus", () => CheckFolderFocus(folder)),
            ("save filter switch during accept", () => CheckFilterSwitchDuringAccept(folder)),
        })
        {
            try { await check(); report.AppendLine(name + ": PASS"); }
            catch (Exception ex) { failures++; report.AppendLine(name + ": FAIL " + ex); }
        }
        File.WriteAllText(Path.Combine(folder, "file-dialog-report.txt"), report.ToString(), Encoding.UTF8);
        App.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    private static async Task CheckConcurrentLoad()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var child = new FolderNode("child", "child", false);
        var node = new FolderNode("root", "root", true)
        {
            ChildEnumerator = _ =>
            {
                entered.Set();
                if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Folder load gate timed out.");
                return [child];
            }
        };
        Task first = node.LoadChildrenAsync();
        try
        {
            await WaitSignal(entered);
            if (node.IsLoaded) throw new InvalidOperationException("The folder reported loaded before its children arrived.");
            Task second = node.LoadChildrenAsync();
            if (second.IsCompleted) throw new InvalidOperationException("A second caller skipped an active folder load.");
            release.Set();
            await Task.WhenAll(first, second);
            if (!node.IsLoaded || node.Children.Count != 1 || node.Children[0] != child)
                throw new InvalidOperationException("Both callers did not observe the loaded child.");
        }
        finally { release.Set(); }
    }

    private static async Task CheckLoadRetry()
    {
        int attempts = 0;
        var child = new FolderNode("child", "child", false);
        var node = new FolderNode("root", "root", true)
        {
            ChildEnumerator = _ => ++attempts == 1 ? throw new IOException("Simulated folder read failure.") : [child]
        };
        try { await node.LoadChildrenAsync(); }
        catch (IOException ex) when (ex.Message == "Simulated folder read failure.") { }
        if (node.IsLoaded) throw new InvalidOperationException("A failed folder load became permanent.");
        await node.LoadChildrenAsync();
        if (attempts != 2 || !node.IsLoaded || node.Children.Count != 1 || node.Children[0] != child)
            throw new InvalidOperationException("The failed folder load could not be retried.");
    }

    private static async Task CheckHiddenRefresh()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var visible = new FolderNode("visible", "visible", false);
        var hidden = new FolderNode("hidden", "hidden", false);
        int reads = 0;
        var node = new FolderNode("root", "root", true)
        {
            ChildEnumerator = _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Initial tree load timed out.");
                    return [visible];
                }
                return [visible, hidden];
            }
        };
        bool original = FolderNode.ShowHidden;
        FolderNode.ShowHidden = false;
        Task load = node.LoadChildrenAsync();
        try
        {
            await WaitSignal(entered);
            FolderNode.ShowHidden = true;
            Task refresh = node.RefreshAsync();
            release.Set();
            await Task.WhenAll(load, refresh);
            if (reads != 2 || node.Children.Count != 2 || !node.Children.Contains(hidden))
                throw new InvalidOperationException("The hidden-folder toggle was lost during the first tree load.");
        }
        finally { release.Set(); FolderNode.ShowHidden = original; }
    }

    private static async Task CheckNavigation(string folder)
    {
        string root = Path.Combine(folder, "fixture");
        string a = Path.Combine(root, "A");
        string deep = Path.Combine(a, "Deep");
        string b = Path.Combine(root, "B");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(b);
        string earlyFile = Path.Combine(root, "early.png");
        File.WriteAllText(earlyFile, "fixture");
        using var listingEntered = new ManualResetEventSlim();
        using var listingRelease = new ManualResetEventSlim();
        using var treeEntered = new ManualResetEventSlim();
        using var treeRelease = new ManualResetEventSlim();
        using var lateEntered = new ManualResetEventSlim();
        using var lateRelease = new ManualResetEventSlim();
        using var acceptEntered = new ManualResetEventSlim();
        using var acceptRelease = new ManualResetEventSlim();
        using var iconEntered = new ManualResetEventSlim();
        using var iconRelease = new ManualResetEventSlim();
        using var bootstrapRelease = new ManualResetEventSlim();
        var initialProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new PickerDialog { InitialDirectory = root, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        dialog.PlaceDiscovery = _ =>
        {
            if (!bootstrapRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Places discovery timed out.");
            return [new PickerDialog.PlaceCandidate(root, "fixture", false)];
        };
        dialog.PlaceIconLoader = path =>
        {
            if (path != root) throw new InvalidOperationException("Icon probe used an unexpected path.");
            iconEntered.Set();
            if (!iconRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Icon gate timed out.");
            return null;
        };
        dialog.TreeDiscovery = () =>
        {
            if (!bootstrapRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Tree discovery timed out.");
            return [new FolderNode(root, "fixture", true)];
        };
        dialog.InitialDirectoryExistsAsync = dir => dir == root
            ? initialProbe.Task : Task.FromResult(Directory.Exists(dir));
        dialog.EntryEnumerator = (dir, _) =>
        {
            if (dir == a)
            {
                listingEntered.Set();
                if (!listingRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Listing gate timed out.");
                return [new PickerEntry("old.txt", Path.Combine(a, "old.txt"), false, 1, DateTime.MinValue)];
            }
            return dir == b
                ? [new PickerEntry("current.txt", Path.Combine(b, "current.txt"), false, 1, DateTime.MinValue)]
                : [];
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = Stopwatch.StartNew();
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                opening.Stop();
                if (opening.ElapsedMilliseconds > 1200)
                    throw new InvalidOperationException("Places or drives delayed the first paint by " + opening.ElapsedMilliseconds + " ms.");
                await Task.Yield();
                var accept = (Button)dialog.FindName("AcceptButton")!;
                var up = (Button)dialog.FindName("UpButton")!;
                if (accept.IsEnabled)
                    throw new InvalidOperationException("Accept was enabled before the first folder was validated.");
                if (up.IsEnabled)
                    throw new InvalidOperationException("Up was enabled before the first folder was validated.");
                up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var nameBox = (TextBox)dialog.FindName("FileNameBox")!;
                nameBox.Text = earlyFile;
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!dialog.IsVisible || dialog.FileName == earlyFile)
                    throw new InvalidOperationException("Accept completed before the first folder was validated.");
                nameBox.Clear();
                initialProbe.SetResult(true);
                bootstrapRelease.Set();
                _ = Task.Run(async () => { await Task.Delay(600); iconRelease.Set(); });
                var iconWatch = Stopwatch.StartNew();
                await WaitSignal(iconEntered);
                iconWatch.Stop();
                if (iconWatch.ElapsedMilliseconds > 300)
                    throw new InvalidOperationException("A real-path shell icon blocked the UI for " + iconWatch.ElapsedMilliseconds + " ms.");
                iconRelease.Set();
                await WaitUntil(() => dialog.Places.Count == 1 && dialog.TreeRoots.Count == 1);
                var box = (TextBox)dialog.FindName("PathBox")!;
                await WaitUntil(() => box.Text == root && accept.IsEnabled);
                _ = Task.Run(async () => { await Task.Delay(600); listingRelease.Set(); });
                var watch = Stopwatch.StartNew();
                EnterPath(box, a);
                watch.Stop();
                await WaitSignal(listingEntered);
                if (watch.ElapsedMilliseconds > 300)
                    throw new InvalidOperationException("A slow directory listing blocked the UI for " + watch.ElapsedMilliseconds + " ms.");
                if (up.IsEnabled)
                    throw new InvalidOperationException("Up was enabled while a folder was loading.");
                EnterPath(box, b);
                await WaitUntil(() => dialog.Entries.Any(x => x.Name == "current.txt"));
                if (!up.IsEnabled)
                    throw new InvalidOperationException("Up did not reenable after a valid folder loaded.");
                listingRelease.Set();
                await Task.Delay(100);
                if (dialog.Entries.Count != 1 || dialog.Entries[0].Name != "current.txt")
                    throw new InvalidOperationException("An older directory listing replaced the current folder.");

                var deepNode = new FolderNode(deep, "Deep", true);
                var aNode = new FolderNode(a, "A", true)
                {
                    ChildEnumerator = _ =>
                    {
                        treeEntered.Set();
                        if (!treeRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Tree gate timed out.");
                        return [deepNode];
                    }
                };
                var bNode = new FolderNode(b, "B", true);
                var rootNode = new FolderNode(root, "fixture", true) { ChildEnumerator = _ => [aNode, bNode] };
                dialog.TreeRoots.Clear();
                dialog.TreeRoots.Add(rootNode);
                EnterPath(box, deep);
                await WaitSignal(treeEntered);
                EnterPath(box, b);
                await WaitUntil(() => bNode.IsSelected);
                treeRelease.Set();
                await WaitUntil(() => aNode.IsLoaded);
                await Task.Delay(100);
                if (deepNode.IsSelected)
                    throw new InvalidOperationException("An older tree reveal selected the previous folder.");

                EnterPath(box, Path.Combine(root, "missing-one"));
                EnterPath(box, Path.Combine(root, "missing-two"));
                await WaitUntil(() => box.Text == b && dialog.Entries.Count == 1
                    && dialog.Entries[0].Name == "current.txt");

                nameBox.Text = "slow-open.png";
                dialog.AcceptFileExists = path =>
                {
                    if (path != Path.Combine(b, "slow-open.png"))
                        throw new InvalidOperationException("Accept probed an unexpected path: " + path);
                    acceptEntered.Set();
                    if (!acceptRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Accept gate timed out.");
                    return false;
                };
                _ = Task.Run(async () => { await Task.Delay(600); acceptRelease.Set(); });
                watch.Restart();
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                watch.Stop();
                await WaitSignal(acceptEntered);
                if (watch.ElapsedMilliseconds > 300)
                    throw new InvalidOperationException("A slow file check blocked the UI for " + watch.ElapsedMilliseconds + " ms.");
                acceptRelease.Set();
                await WaitUntil(() => accept.IsEnabled);
                if (!dialog.IsVisible)
                    throw new InvalidOperationException("A missing file closed the picker.");

                string late = Path.Combine(root, "Late");
                Directory.CreateDirectory(late);
                var normalEnumerator = dialog.EntryEnumerator;
                dialog.EntryEnumerator = (dir, hidden) =>
                {
                    if (dir != late) return normalEnumerator(dir, hidden);
                    lateEntered.Set();
                    if (!lateRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Closed dialog listing timed out.");
                    return [new PickerEntry("late.txt", Path.Combine(late, "late.txt"), false, 1, DateTime.MinValue)];
                };
                EnterPath(box, late);
                await WaitSignal(lateEntered);
                dialog.Close();
                lateRelease.Set();
                await Task.Delay(150);
                if (dialog.Entries.Any(x => x.Name == "late.txt") ||
                    (ThemeManager.GetSetting("FileDlgRecents") ?? "").Split('|').Contains(late))
                    throw new InvalidOperationException("A closed dialog applied its pending folder listing.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { initialProbe.TrySetResult(false); bootstrapRelease.Set(); listingRelease.Set(); treeRelease.Set(); lateRelease.Set(); acceptRelease.Set(); iconRelease.Set(); if (dialog.IsVisible) dialog.Close(); }
        };
        _ = Task.Run(async () => { await Task.Delay(1500); bootstrapRelease.Set(); });
        dialog.ShowDialog(null);
        await completion.Task;
    }

    private static async Task CheckFolderFocus(string folder)
    {
        string root = Path.Combine(folder, "folder-focus");
        Directory.CreateDirectory(root);
        var dialog = new PickerDialog(FileDialogMode.Folder) { InitialDirectory = root,
            Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false };
        dialog.PlaceDiscovery = _ => [];
        dialog.TreeDiscovery = () => [];
        dialog.InitialDirectoryExistsAsync = dir => Task.FromResult(dir == root);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var accept = (Button)dialog.FindName("AcceptButton")!;
                await WaitUntil(() => accept.IsEnabled);
                if (!accept.IsKeyboardFocused)
                    throw new InvalidOperationException("The folder picker did not focus Select after its first valid folder loaded.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        };
        dialog.ShowDialog(null);
        await completion.Task;
    }

    private static async Task CheckFilterSwitchDuringAccept(string folder)
    {
        string root = Path.Combine(folder, "filter-switch");
        Directory.CreateDirectory(root);
        using var probeEntered = new ManualResetEventSlim();
        using var probeRelease = new ManualResetEventSlim();
        int probes = 0;
        var dialog = new PickerDialog(FileDialogMode.Save)
        {
            InitialDirectory = root,
            FileName = "capture",
            Filter = "PNG|*.png|JPEG|*.jpg",
            OverwritePrompt = false,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false
        };
        dialog.PlaceDiscovery = _ => [];
        dialog.TreeDiscovery = () => [];
        dialog.EntryEnumerator = (_, _) => [];
        dialog.InitialDirectoryExistsAsync = dir => Task.FromResult(dir == root);
        dialog.AcceptDirectoryExists = dir =>
        {
            if (dir != root) throw new InvalidOperationException("Save probed an unexpected folder.");
            if (Interlocked.Increment(ref probes) == 1)
            {
                probeEntered.Set();
                if (!probeRelease.Wait(TimeSpan.FromSeconds(3)))
                    throw new TimeoutException("Save directory probe timed out.");
            }
            return true;
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var accept = (Button)dialog.FindName("AcceptButton")!;
                var filter = (ComboBox)dialog.FindName("FilterCombo")!;
                await WaitUntil(() => accept.IsEnabled);
                if (filter.SelectedIndex != 0)
                    throw new InvalidOperationException("Save did not start with the PNG filter.");
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitSignal(probeEntered);
                filter.SelectedIndex = 1;
                probeRelease.Set();
                await WaitUntil(() => accept.IsEnabled || !dialog.IsVisible);
                if (!dialog.IsVisible)
                    throw new InvalidOperationException("Save accepted the old extension after the filter changed.");
                accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntil(() => !dialog.IsVisible);
                if (dialog.FileName != Path.Combine(root, "capture.jpg"))
                    throw new InvalidOperationException("Retry did not use the selected JPEG filter.");
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally { probeRelease.Set(); if (dialog.IsVisible) dialog.Close(); }
        };
        dialog.ShowDialog(null);
        await completion.Task;
    }

    private static void EnterPath(TextBox box, string path)
    {
        box.Text = path;
        var source = PresentationSource.FromVisual(box)
            ?? throw new InvalidOperationException("The path box is not rendered.");
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        box.RaiseEvent(key);
    }

    private static async Task WaitSignal(ManualResetEventSlim signal)
    {
        if (!await Task.Run(() => signal.Wait(TimeSpan.FromSeconds(3))))
            throw new TimeoutException("The expected background operation did not start.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(3)) throw new TimeoutException("The expected UI state did not appear.");
            await Task.Delay(20);
        }
    }
}
