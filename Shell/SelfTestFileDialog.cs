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
        string b = Path.Combine(root, "B");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);
        using var listingEntered = new ManualResetEventSlim();
        using var listingRelease = new ManualResetEventSlim();
        using var lateEntered = new ManualResetEventSlim();
        using var lateRelease = new ManualResetEventSlim();
        var dialog = new PickerDialog { InitialDirectory = root, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowInTaskbar = false };
        string late = Path.Combine(root, "Late");
        Directory.CreateDirectory(late);
        dialog.DirectoryListingReader = (dir, _, _) =>
        {
            if (dir == a)
            {
                listingEntered.Set();
                if (!listingRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Listing gate timed out.");
                return new PickerListingResult([new PickerEntry("old.txt", Path.Combine(a, "old.txt"), false, 1, DateTime.MinValue)], true, true);
            }
            if (dir == late)
            {
                lateEntered.Set();
                if (!lateRelease.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Closed dialog listing timed out.");
                return new PickerListingResult([new PickerEntry("late.txt", Path.Combine(late, "late.txt"), false, 1, DateTime.MinValue)], true, true);
            }
            if (!Directory.Exists(dir)) return new PickerListingResult([], false, false);
            return dir == b
                ? new PickerListingResult([new PickerEntry("current.txt", Path.Combine(b, "current.txt"), false, 1, DateTime.MinValue)], true, true)
                : new PickerListingResult([], true, true);
        };
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var accept = (Button)dialog.FindName("AcceptButton")!;
                var up = (Button)dialog.FindName("UpButton")!;
                var box = (TextBox)dialog.FindName("PathBox")!;
                await WaitUntil(() => box.Text == root && accept.IsEnabled);
                _ = Task.Run(async () => { await Task.Delay(600); listingRelease.Set(); });
                var watch = Stopwatch.StartNew();
                EnterPath(box, a);
                watch.Stop();
                await WaitSignal(listingEntered);
                if (watch.ElapsedMilliseconds > 300)
                    throw new InvalidOperationException("A slow directory listing blocked the UI for " + watch.ElapsedMilliseconds + " ms.");
                if (accept.IsEnabled || up.IsEnabled)
                    throw new InvalidOperationException("Accept or Up was enabled while a folder was loading.");
                EnterPath(box, b);
                await WaitUntil(() => dialog.Entries.Any(x => x.Name == "current.txt"));
                if (!up.IsEnabled || !accept.IsEnabled)
                    throw new InvalidOperationException("Accept or Up did not reenable after a valid folder loaded.");
                listingRelease.Set();
                await Task.Delay(100);
                if (dialog.Entries.Count != 1 || dialog.Entries[0].Name != "current.txt")
                    throw new InvalidOperationException("An older directory listing replaced the current folder.");

                EnterPath(box, Path.Combine(root, "missing-one"));
                EnterPath(box, Path.Combine(root, "missing-two"));
                await WaitUntil(() => box.Text == b && accept.IsEnabled && dialog.Entries.Count == 1
                    && dialog.Entries[0].Name == "current.txt");

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
            finally { listingRelease.Set(); lateRelease.Set(); if (dialog.IsVisible) dialog.Close(); }
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
