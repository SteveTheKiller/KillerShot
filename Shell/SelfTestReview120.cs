using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Resources;
using System.Text;
using System.Text.Json;
using System.Windows;
using KillerShot.Files;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>
/// Headless check: KillerShot.exe --selftest-review-120 &lt;new folder&gt;. Covers the 1.2.0 release
/// review: every language carries the newer-album message, a safe save in a watched folder keeps
/// the saved file listed, a password change and an album rename treat decrypted playback copies
/// correctly, and settings writes keep a newer value from another process and preserve a damaged
/// file once.
/// </summary>
internal static class SelfTestReview120
{
    internal static bool TryRun(string[] args)
    {
        if (args is not ["--selftest-review-120", var folder]) return false;
        string dir = Path.GetFullPath(folder);
        if (Directory.Exists(dir)) { Application.Current.Shutdown(2); return true; }
        Directory.CreateDirectory(dir);
        SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(dir, "native"));
        Settings.UseFolder(Path.Combine(dir, "settings"));
        VideoCaptures.UsePlaybackFolder(Path.Combine(dir, "playback"));
        CaptureLibrary.BackgroundWork = false;
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _ = RunAsync(dir);
        return true;
    }

    private static async Task RunAsync(string dir)
    {
        var log = new StringBuilder();
        int failures = 0;
        async Task Check(string id, Func<Task> body)
        {
            Settings.UseFolder(Path.Combine(dir, "settings-" + id));
            Settings.Set("ActiveAlbum", "Library.kshot");
            CaptureLibrary.UseFolder(Path.Combine(dir, "lib-" + id));
            try { await body(); log.AppendLine($"{id}=pass"); }
            catch (Exception error) { failures++; log.AppendLine($"{id}=FAIL {error.GetType().Name}: {error.Message}"); }
            finally { try { CaptureLibrary.Close(); } catch { } }
        }
        Task Sync(Action body) { body(); return Task.CompletedTask; }

        await Check("RV-001", () => Sync(EveryLanguageNamesNewerAlbums));
        await Check("RV-002", () => SafeSaveKeepsFileListed(dir));
        await Check("RV-003", () => Task.Run(() => PasswordChangeRemovesPlaybackCopies(dir)));
        await Check("RV-004", () => Task.Run(() => RenameKeepsLivePlaybackCopies(dir)));
        await Check("RV-005", () => Sync(() => DamagedSettingsPreservedOnce(dir)));
        await Check("RV-006", () => Sync(() => StalePendingDoesNotOverwriteNewerValue(dir)));

        log.AppendLine(failures == 0 ? "result=ok" : $"result=failed {failures}");
        File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString());
        Application.Current.Shutdown(failures == 0 ? 0 : 1);
    }

    // ---- RV-001 (BF-001): the newer-album refusal is a sentence in every language ----

    private static void EveryLanguageNamesNewerAlbums()
    {
        var assembly = typeof(App).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetName().Name + ".g.resources")
            ?? throw new InvalidOperationException("The compiled XAML resources are missing.");
        using var reader = new ResourceReader(stream);
        var files = new List<string>();
        foreach (DictionaryEntry entry in reader)
            if (entry.Key is string name && name.StartsWith("strings/", StringComparison.Ordinal) && name.EndsWith(".baml", StringComparison.Ordinal))
                files.Add(name[..^5] + ".xaml");
        Require(files.Count >= 20, $"Only {files.Count} language files were found.");
        var missing = new List<string>();
        foreach (string file in files)
        {
            var dictionary = (ResourceDictionary)Application.LoadComponent(new Uri("/KillerShot;component/" + file, UriKind.Relative));
            if (dictionary["Str_Album_NewerSchema"] is not string text || string.IsNullOrWhiteSpace(text)) missing.Add(file);
        }
        Require(missing.Count == 0, "Str_Album_NewerSchema is missing from " + string.Join(", ", missing));
    }

    // ---- RV-002 (BF-F01): a safe save (A to A.bak, tmp to A, delete A.bak) keeps A listed ----

    private static async Task SafeSaveKeepsFileListed(string dir)
    {
        for (int trial = 0; trial < 5; trial++)
        {
            string watched = Directory.CreateDirectory(Path.Combine(dir, "watch-" + trial)).FullName;
            string file = Path.Combine(watched, "notes.txt");
            File.WriteAllText(file, "first");
            using var browser = new Browser();
            Require(await browser.NavigateTo(watched), "Fixture navigation failed.");
            Require(browser.State.Results.Any(e => e.FilePath == file), "The fixture file was not listed.");
            string temp = Path.Combine(watched, "notes.txt.tmp");
            string backup = Path.Combine(watched, "notes.txt.bak");
            File.WriteAllText(temp, "second, longer");
            File.Move(file, backup);
            File.Move(temp, file);
            File.Delete(backup);
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(50);
            bool listed = browser.State.Results.Any(e => string.Equals(e.FilePath, file, StringComparison.OrdinalIgnoreCase));
            bool stray = browser.State.Results.Any(e => e.FilePath == temp || e.FilePath == backup);
            Require(listed, $"Trial {trial}: the safely saved file vanished from the listing.");
            Require(!stray, $"Trial {trial}: a temporary or backup name is still listed.");
        }
    }

    // ---- RV-003 (BF-R01): setting an album password removes the decrypted playback copies ----

    private static void PasswordChangeRemovesPlaybackCopies(string dir)
    {
        Open();
        long video = CaptureLibrary.AddVideo(FakeClip(dir, "before-password", 4096), DateTime.Now, "clip");
        string copy = VideoCaptures.DecryptForPlayback(video);
        Require(File.Exists(copy), "No playback copy was made.");
        CaptureLibrary.SetPassword("review-120");
        var wait = Stopwatch.StartNew();
        while (File.Exists(copy) && wait.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(50);
        Require(!File.Exists(copy), "A decrypted recording stayed on disk after the album got a password.");
    }

    // ---- RV-004 (BF-R02): renaming the open album keeps its playback copies usable ----

    private static void RenameKeepsLivePlaybackCopies(string dir)
    {
        Open();
        long video = CaptureLibrary.AddVideo(FakeClip(dir, "before-rename", 4096), DateTime.Now, "clip");
        string copy = VideoCaptures.DecryptForPlayback(video);
        Require(File.Exists(copy), "No playback copy was made.");
        CaptureLibrary.RenameAlbum(CaptureLibrary.FilePath, "Renamed120");
        Thread.Sleep(1000);
        Require(File.Exists(copy), "Renaming the open album deleted a playback copy of the same, still open album.");
        Require(VideoCaptures.DecryptForPlayback(video) == copy, "The renamed album's playback copy was not reused.");
    }

    // ---- RV-005 (BF-U04): a damaged settings file is set aside once, and saving goes on ----

    private static void DamagedSettingsPreservedOnce(string dir)
    {
        string folder = Path.Combine(dir, "settings-damaged");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "settings.json");
        File.WriteAllText(file, "{ not json");
        Settings.UseFolder(folder);
        // A program holding the damaged file (an antivirus scan, say) makes every save fail.
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Settings.Set("A", "1");
            Settings.Set("B", "2");
            Settings.Set("C", "3");
        }
        int copies = Directory.GetFiles(folder, "settings.json.corrupt-*").Length;
        Require(copies == 1, $"The damaged settings file was preserved {copies} times, expected once.");
        Settings.SetRequired("C", "3");
        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Require(saved.GetValueOrDefault("A") == "1" && saved.GetValueOrDefault("C") == "3", "Settings written after the damage were lost.");
    }

    // ---- RV-006 (BF-U03): a value that failed to save never overwrites a newer one ----

    private static void StalePendingDoesNotOverwriteNewerValue(string dir)
    {
        string folder = Path.Combine(dir, "settings-pending");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "settings.json");
        Settings.UseFolder(folder);
        Settings.SetRequired("Theme", "Dark");
        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Settings.Set("Theme", "Light");
        // Another KillerShot saves a newer choice in between.
        File.WriteAllText(file, JsonSerializer.Serialize(new Dictionary<string, string> { ["Theme"] = "Ocean" }));
        Settings.SetRequired("Other", "x");
        var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))!;
        Require(saved.GetValueOrDefault("Theme") == "Ocean", $"A stale unsaved value replaced the newer one: Theme={saved.GetValueOrDefault("Theme")}.");
        Require(saved.GetValueOrDefault("Other") == "x", "The later setting was not saved.");
    }

    // ---- helpers ----

    private static void Open()
    {
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The scratch album did not open: " + CaptureLibrary.LoadError);
    }

    private static string FakeClip(string dir, string name, int bytes)
    {
        string folder = Path.Combine(dir, "clips");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".mp4");
        var data = new byte[bytes];
        new Random(bytes ^ name.Length).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
