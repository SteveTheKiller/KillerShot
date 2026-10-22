using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

/// <summary>Library check: KillerShot.exe --selftest-library &lt;folder&gt;. Builds an encrypted
/// library of generated pictures in the folder, checks storage, search, text recognition,
/// collections, the history limit and the password, then renders each library view off screen.
/// Settings and the library live in the output folder, so the user's own library is untouched,
/// and nothing on screen is captured.</summary>
internal static partial class SelfTestLibrary
{
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    public static bool TryRun(string[] args)
    {
        if (SelfTestScreenshotsLoading.TryRun(args)) return true;
        if (SelfTestFolderEnumeration.TryRun(args)) return true;
        if (args.Length == 2 && args[0] == "--selftest-folder-removal")
        {
            _ = RunFolderRemoval(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length == 2 && args[0] == "--selftest-thumbnail-loading")
        {
            _ = RunThumbnailLoading(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length == 2 && args[0] == "--selftest-folder-fixes")
        {
            _ = RunFolderFixes(Path.GetFullPath(args[1]));
            return true;
        }
        if (args.Length == 4 && args[0] == "--selftest-folder-probe")
        {
            _ = ProbeFolderCollections(args[1], args[2], args[3]);
            return true;
        }
        if (args.Length == 2 && args[0] == "--selftest-folder-collections")
        {
            _ = RunFolderCollections(Path.GetFullPath(args[1]));
            return true;
        }
        if (SelfTestStorageSafety.TryRun(args)) return true;
        if (SelfTestCliSafety.TryRun(args)) return true;
        if (args.Length == 3 && args[0].Equals("--selftest-library-native-probe", StringComparison.OrdinalIgnoreCase))
        {
            SetErrorMode(0x0001); // Keep a bad native test fixture from opening a Windows error dialog.
            string cache = Path.GetFullPath(args[1]);
            string report = Path.GetFullPath(args[2]);
            int exitCode = 0;
            try
            {
                SqlCipherBootstrap.UseCacheFolderForTests(cache);
                Settings.UseFolder(Path.Combine(Path.GetDirectoryName(report)!, "settings"));
                CaptureLibrary.UseFolder(Path.Combine(Path.GetDirectoryName(report)!, "library"));
                CaptureLibrary.BackgroundWork = false;
                if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError ?? "Album did not open.");
                CaptureLibrary.Close();
                File.WriteAllText(report, "ok");
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); exitCode = 1; }
            Application.Current.Shutdown(exitCode);
            return true;
        }
        if (args.Length == 4 && args[0].Equals("--selftest-library-open-probe", StringComparison.OrdinalIgnoreCase))
        {
            string library = Path.GetFullPath(args[1]);
            string report = Path.GetFullPath(args[2]);
            SqlCipherBootstrap.UseCacheFolderForTests(args[3]);
            Settings.UseFolder(Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + ".settings"));
            CaptureLibrary.UseFolder(library);
            CaptureLibrary.BackgroundWork = false;
            int exitCode = 0;
            try
            {
                File.WriteAllText(report + ".ready", "ready");
                if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError ?? "Album did not open.");
                var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
                pixel.Freeze();
                CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Process probe", null);
                CaptureLibrary.Close();
                File.WriteAllText(report, "ok");
            }
            catch (Exception ex) { File.WriteAllText(report, ex.ToString()); exitCode = 1; }
            Application.Current.Shutdown(exitCode);
            return true;
        }
        int focused = Array.FindIndex(args, a => a.Equals("--selftest-library-fixes", StringComparison.OrdinalIgnoreCase));
        if (focused >= 0)
        {
            string output = focused + 1 < args.Length ? args[focused + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-library-fixes");
            Directory.CreateDirectory(output);
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(output, "native-parent"));
            Settings.UseFolder(Path.Combine(output, "settings"));
            App.InitializeKit();
            _ = RunFocused(output);
            return true;
        }
        int i = Array.FindIndex(args, a => a.Equals("--selftest-library", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        string dir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "KillerShot-selftest-library");
        Directory.CreateDirectory(dir);
        string lib = Path.Combine(dir, "library");
        if (Directory.Exists(lib)) Directory.Delete(lib, true);
        Settings.UseFolder(Path.Combine(dir, "settings"));
        CaptureLibrary.UseFolder(lib);
        string shots = Path.Combine(dir, "Screenshots");
        if (Directory.Exists(shots)) Directory.Delete(shots, true);
        Directory.CreateDirectory(shots);
        FolderCaptures.UseFolder(shots);
        App.InitializeKit();
        _ = RunAsync(dir, lib);
        return true;
    }

    private static readonly (string Title, string Process, string Text, Color Tint)[] Samples =
    [
        ("Invoice 4471 - Contoso Billing", "msedge", "Invoice 4471 total 129.00", Color.FromRgb(0x1E, 0x88, 0xE5)),
        ("Build failed - Pipelines", "msedge", "Build failed on step restore", Color.FromRgb(0xE5, 0x39, 0x35)),
        ("Device Manager", "mmc", "Network adapters", Color.FromRgb(0x43, 0xA0, 0x47)),
        ("Mockup v3 - Figma", "figma", "Landing page hero", Color.FromRgb(0x8E, 0x24, 0xAA)),
        ("Event Viewer", "mmc", "Event 7031 service crashed", Color.FromRgb(0xFB, 0x8C, 0x00)),
        ("Settings", "SystemSettings", "Display scale 150 percent", Color.FromRgb(0x2E, 0xC4, 0xB6)),
    ];

    private static async Task RunFocused(string dir)
    {
        var log = new StringBuilder();
        int code = 0;
        try
        {
            CaptureLibrary.BackgroundWork = false;
            bool nativeCacheRecovered = await TestNativeCacheRecovery(dir);
            log.AppendLine($"native_cache_recovered={nativeCacheRecovered}");
            if (!nativeCacheRecovered) throw new InvalidOperationException("A same-size corrupt native cache was accepted.");
            bool crossProcessOpen = await TestCrossProcessOpen(dir);
            log.AppendLine($"cross_process_first_open={crossProcessOpen}");
            if (!crossProcessOpen) throw new InvalidOperationException("Two processes did not safely open one new album.");
            bool passwordSwapWaited = await TestPasswordSwapWait(dir);
            log.AppendLine($"password_swap_waited={passwordSwapWaited}");
            if (!passwordSwapWaited) throw new InvalidOperationException("A password swap bypassed the album lock.");
            string lib = Path.Combine(dir, "recovery");
            CaptureLibrary.UseFolder(lib);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Recovery fixture did not open.");
            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            pixel.Freeze();
            CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Recovery", null);
            CaptureLibrary.Close();
            string backup = CaptureLibrary.FilePath + ".bak";
            File.Move(CaptureLibrary.FilePath, backup);
            bool recovered = CaptureLibrary.OpenAuto() && CaptureLibrary.Count == 1
                && File.Exists(CaptureLibrary.FilePath) && !File.Exists(backup);
            log.AppendLine($"missing_active_backup_recovered={recovered}");
            if (!recovered) throw new InvalidOperationException("A missing active album did not recover from its backup.");
            CaptureLibrary.Close();

            string protectedDir = Path.Combine(dir, "password-recovery");
            CaptureLibrary.UseFolder(protectedDir);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Password recovery fixture did not open.");
            CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Protected recovery", null);
            CaptureLibrary.SetPassword("recovery pass");
            CaptureLibrary.Close();
            backup = CaptureLibrary.FilePath + ".bak";
            File.Move(CaptureLibrary.FilePath, backup);
            bool remainedLocked = !CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword && File.Exists(backup);
            bool recoveredProtected = remainedLocked && CaptureLibrary.Unlock("recovery pass")
                && CaptureLibrary.Count == 1 && File.Exists(CaptureLibrary.FilePath) && !File.Exists(backup);
            log.AppendLine($"password_backup_recovered={recoveredProtected}");
            if (!recoveredProtected) throw new InvalidOperationException("A protected album did not recover from its backup.");
            CaptureLibrary.Close();

            string shots = Path.Combine(dir, "Screenshots");
            Directory.CreateDirectory(shots);
            FolderCaptures.UseFolder(shots);
            string removed = Path.Combine(shots, "removed.png");
            string renamed = Path.Combine(shots, "renamed.png");
            string kept = Path.Combine(shots, "kept.png");
            ImageOutput.SavePng(pixel, removed);
            ImageOutput.SavePng(pixel, renamed);
            ImageOutput.SavePng(pixel, kept);
            var before = FolderCaptures.Items();
            long removedId = before.Single(i => i.Window == "removed").Id;
            long renamedId = before.Single(i => i.Window == "renamed").Id;
            long keptId = before.Single(i => i.Window == "kept").Id;
            if (FolderCaptures.LoadThumb(removedId) == null) throw new InvalidOperationException("Thumbnail fixture did not load.");
            File.Delete(removed);
            File.Move(renamed, Path.Combine(shots, "new-name.png"));
            var after = FolderCaptures.Items("kept");
            bool pruned = after.Count == 1 && after[0].Id == keptId
                && FolderCaptures.PathOf(removedId) == null && FolderCaptures.PathOf(renamedId) == null
                && FolderCaptures.LoadThumb(removedId) == null;
            log.AppendLine($"folder_cache_pruned={pruned}");
            if (!pruned) throw new InvalidOperationException("Removed folder files still have cached IDs or thumbnails.");

            string unlockDir = Path.Combine(dir, "unlock-error");
            CaptureLibrary.UseFolder(unlockDir);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Unlock fixture did not open.");
            CaptureLibrary.SetPassword("test pass");
            CaptureLibrary.Close();
            string cs = new SqliteConnectionStringBuilder { DataSource = CaptureLibrary.FilePath, Password = "test pass" }.ConnectionString;
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TABLE meta; CREATE VIEW meta AS SELECT 'schema' AS key, 'broken' AS value";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            if (CaptureLibrary.OpenAuto() || !CaptureLibrary.NeedsPassword) throw new InvalidOperationException("Unlock fixture did not lock.");
            ThemeManager.Apply(Theme.Dark);
            LocaleManager.Apply(Locale.EnUS);
            var view = new LibraryView();
            var window = new Window { Content = view, Width = 640, Height = 480, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            window.Show();
            var passwordBox = (PasswordBox)view.FindName("UnlockBox")!;
            var unlockButton = (Button)view.FindName("UnlockBtn")!;
            var unlockError = (TextBlock)view.FindName("UnlockError")!;
            bool dispatcherError = false;
            System.Windows.Threading.DispatcherUnhandledExceptionEventHandler onDispatcherError = (_, e) =>
            {
                dispatcherError = true;
                e.Handled = true;
            };
            Application.Current.DispatcherUnhandledException += onDispatcherError;
            passwordBox.Password = "test pass";
            unlockButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (int attempt = 0; attempt < 100 && unlockError.Visibility != Visibility.Visible && !dispatcherError; attempt++)
                await Task.Delay(50);
            Application.Current.DispatcherUnhandledException -= onDispatcherError;
            bool unlockErrorShown = !dispatcherError && !CaptureLibrary.IsOpen
                && unlockError.Visibility == Visibility.Visible
                && unlockError.Text.StartsWith("The library could not be opened:", StringComparison.Ordinal);
            log.AppendLine($"unlock_error_shown={unlockErrorShown} dispatcher_error={dispatcherError} unlock_visible={unlockError.Visibility} unlock_text={unlockError.Text}");
            window.Close();
            if (!unlockErrorShown) throw new InvalidOperationException("A library unlock error escaped the screen.");

            string headerDir = Path.Combine(dir, "header-menu");
            CaptureLibrary.UseFolder(headerDir);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Header fixture did not open.");
            long collection = CaptureLibrary.CreateCollection("Keyboard collection");
            var headerView = new LibraryView();
            var header = headerView.BuildRow(new LibraryRow
            {
                Kind = LibraryRowKind.Header, Title = "Keyboard collection", Detail = "0 captures",
                IsCollection = true, CollectionId = collection,
            });
            var headerWindow = new Window { Content = header, Width = 640, Height = 160, ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            headerWindow.Show();
            var menuKey = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(header), Environment.TickCount, System.Windows.Input.Key.Apps)
            { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent };
            header.RaiseEvent(menuKey);
            bool keyboardMenu = header.Focusable && System.Windows.Input.KeyboardNavigation.GetIsTabStop(header)
                && header.ContextMenu?.IsOpen == true
                && header.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Rename collection..."));
            log.AppendLine($"collection_header_keyboard_menu={keyboardMenu}");
            if (header.ContextMenu != null) header.ContextMenu.IsOpen = false;
            headerWindow.Close();
            CaptureLibrary.Close();
            if (!keyboardMenu) throw new InvalidOperationException("A collection header menu cannot be opened with the keyboard.");
            log.AppendLine("result=ok");
        }
        catch (Exception ex)
        {
            log.AppendLine($"result=error {ex}");
            code = 1;
        }
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString()); } catch { }
        Application.Current.Shutdown(code);
    }

    private static async Task<bool> TestCrossProcessOpen(string dir)
    {
        string root = Path.Combine(dir, "process-race");
        string library = Path.Combine(root, "library");
        string nativeCache = Path.Combine(root, "native-cache");
        Directory.CreateDirectory(library);
        string firstReport = Path.Combine(root, "first.txt");
        string secondReport = Path.Combine(root, "second.txt");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable.");
        System.Diagnostics.Process Start(string report)
        {
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            start.ArgumentList.Add("--selftest-library-open-probe");
            start.ArgumentList.Add(library);
            start.ArgumentList.Add(report);
            start.ArgumentList.Add(nativeCache);
            return System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Open probe did not start.");
        }
        System.Diagnostics.Process? first = null;
        System.Diagnostics.Process? second = null;
        try
        {
            bool blocked;
            using (new FileStream(Path.Combine(library, "Library.open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                first = Start(firstReport);
                second = Start(secondReport);
                for (int attempt = 0; attempt < 250 &&
                    (!File.Exists(firstReport + ".ready") || !File.Exists(secondReport + ".ready")); attempt++)
                    await Task.Delay(20);
                if (!File.Exists(firstReport + ".ready") || !File.Exists(secondReport + ".ready"))
                    throw new InvalidOperationException("Open probes did not reach the shared album.");
                await Task.Delay(350);
                blocked = !File.Exists(Path.Combine(library, "Library.key")) &&
                    !File.Exists(Path.Combine(library, "Library.kshot")) &&
                    !first.HasExited && !second.HasExited;
            }
            if (!blocked) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            if (first.ExitCode != 0 || second.ExitCode != 0 ||
                File.ReadAllText(firstReport) != "ok" || File.ReadAllText(secondReport) != "ok") return false;
            string nativeDir = Path.Combine(nativeCache, typeof(SqlCipherBootstrap).Assembly.GetName().Version?.ToString() ?? "0");
            if (!File.Exists(Path.Combine(nativeDir, "e_sqlcipher.dll")) || Directory.EnumerateFiles(nativeDir, "*.tmp").Any()) return false;
            CaptureLibrary.UseFolder(library);
            bool reopened = CaptureLibrary.OpenAuto() && CaptureLibrary.Count == 2;
            CaptureLibrary.Close();
            return reopened;
        }
        finally
        {
            if (first is { HasExited: false }) first.Kill(entireProcessTree: true);
            if (second is { HasExited: false }) second.Kill(entireProcessTree: true);
            first?.Dispose();
            second?.Dispose();
        }
    }

    private static async Task<bool> TestNativeCacheRecovery(string dir)
    {
        string root = Path.Combine(dir, "native-corrupt-cache");
        string cache = Path.Combine(root, "cache");
        string version = typeof(SqlCipherBootstrap).Assembly.GetName().Version?.ToString() ?? "0";
        string nativeDir = Path.Combine(cache, version);
        Directory.CreateDirectory(nativeDir);
        using var resource = typeof(SqlCipherBootstrap).Assembly.GetManifestResourceStream("KillerShot.SqlCipherNative.e_sqlcipher.dll")
            ?? throw new InvalidOperationException("Native fixture resource is unavailable.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        byte[] expectedHash = SHA256.HashData(bytes.ToArray());
        string target = Path.Combine(nativeDir, "e_sqlcipher.dll");
        File.WriteAllBytes(target, new byte[bytes.Length]);
        string report = Path.Combine(root, "probe.txt");
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("--selftest-library-native-probe");
        start.ArgumentList.Add(cache);
        start.ArgumentList.Add(report);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Native probe did not start.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0 && File.ReadAllText(report) == "ok" &&
                SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(expectedHash) &&
                !Directory.EnumerateFiles(nativeDir, "*.tmp").Any();
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }

    private static async Task<bool> TestPasswordSwapWait(string dir)
    {
        string library = Path.Combine(dir, "password-swap-lock");
        CaptureLibrary.UseFolder(library);
        if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Password swap fixture did not open.");
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        pixel.Freeze();
        CaptureLibrary.AddNow(pixel, DateTime.Now, "region", "Swap lock", null);
        string albumPath = CaptureLibrary.FilePath;
        bool blocked;
        Task swap;
        using (new FileStream(Path.Combine(library, "Library.open.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            swap = Task.Run(() => CaptureLibrary.SetPassword("swap pass"));
            await Task.Delay(350);
            blocked = !swap.IsCompleted && !File.Exists(albumPath + ".rekey") &&
                !File.Exists(Path.Combine(library, "Library.key.next"));
        }
        await swap.WaitAsync(TimeSpan.FromSeconds(20));
        CaptureLibrary.Close();
        bool protectedAlbum = !CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword &&
            CaptureLibrary.Unlock("swap pass") && CaptureLibrary.Count == 1;
        CaptureLibrary.Close();
        return blocked && protectedAlbum;
    }

    private static async Task RunAsync(string dir, string lib)
    {
        var log = new StringBuilder();
        int code = 0;
        try
        {
            var now0 = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 9, 15, 0);
            ThemeManager.Apply(Theme.Dark);
            ThemeManager.ApplyAccent(Theme.Dark, Accent.Teal);
            LocaleManager.Apply(Locale.EnUS);

            // A valid keyed file with an invalid schema must not become the open album.
            string schemaDir = Path.Combine(dir, "schema-failure");
            CaptureLibrary.UseFolder(schemaDir);
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Schema fixture did not open.");
            CaptureLibrary.Close();
            byte[] entropy = "KillerShot.Library.v1"u8.ToArray();
            string schemaKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                File.ReadAllBytes(Path.Combine(schemaDir, "Library.key")), entropy, DataProtectionScope.CurrentUser));
            var schemaCs = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(schemaDir, "Library.kshot"), Password = schemaKey
            }.ConnectionString;
            using (var db = new SqliteConnection(schemaCs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TABLE meta; CREATE VIEW meta AS SELECT 'schema' AS key, 'broken' AS value";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            bool schemaRejected = !CaptureLibrary.OpenAuto() && !CaptureLibrary.IsOpen && CaptureLibrary.LoadError != null;
            log.AppendLine($"schema_rejected={schemaRejected}");
            if (!schemaRejected) throw new InvalidOperationException("A failed schema left the album open.");
            using (var db = new SqliteConnection(schemaCs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP VIEW meta; CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("The album did not retry after schema repair.");
            CaptureLibrary.UseFolder(lib);
            CaptureLibrary.BackgroundWork = true;

            // The old plain-PNG history, in the test folder's own Recent folder: the first open
            // imports it and removes the files.
            string recent = Path.Combine(lib, "Recent");
            Directory.CreateDirectory(recent);
            var old = new[] { now0.AddDays(-30), now0.AddDays(-29) };
            foreach (var t0 in old)
                ImageOutput.SavePng(Picture(50, "Old history", "Imported from Recent", Colors.DimGray),
                    Path.Combine(recent, t0.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture) + ".png"));

            // Open: a new library and its sealed key.
            bool opened = await CaptureLibrary.BeginOpen();
            log.AppendLine($"open={opened} key_file={File.Exists(Path.Combine(lib, "Library.key"))}");
            if (!opened) throw new InvalidOperationException("New library did not open: " + CaptureLibrary.LoadError);
            for (int t = 0; t < 40 && Directory.Exists(recent); t++) await Task.Delay(250);
            int imported = CaptureLibrary.Count;
            log.AppendLine($"imported_recent={imported} recent_folder_removed={!Directory.Exists(recent)}");
            if (imported != 2 || Directory.Exists(recent)) throw new InvalidOperationException("The old history was not imported.");

            // Store: 36 pictures over three weeks, in three collections and outside them.
            long bugs = CaptureLibrary.CreateCollection("Bug reports");
            long design = CaptureLibrary.CreateCollection("Design");
            long receipts = CaptureLibrary.CreateCollection("Receipts");
            var now = DateTime.Now;
            var ids = new List<long>();
            BitmapSource? firstStored = null;
            for (int n = 0; n < 36; n++)
            {
                var s = Samples[n % Samples.Length];
                var bmp = Picture(n, s.Title, s.Text, s.Tint);
                firstStored ??= bmp;
                var taken = now.AddHours(-n * 13.5);
                ids.Add(CaptureLibrary.AddNow(bmp, taken, n % 4 == 0 ? "window" : "region", s.Title, s.Process));
            }
            CaptureLibrary.Move(ids.Where((_, k) => k % 6 == 1 || k % 6 == 4), bugs);
            CaptureLibrary.Move(ids.Where((_, k) => k % 6 == 3), design);
            CaptureLibrary.Move(ids.Where((_, k) => k % 6 == 0).Take(4), receipts);
            CaptureLibrary.SetNote(ids[3], "Hero spacing feedback from Steve");
            log.AppendLine($"count={CaptureLibrary.Count} collections={string.Join(",", CaptureLibrary.Collections().Select(c => c.Name + ":" + c.Count))}");
            if (CaptureLibrary.Count != 38) throw new InvalidOperationException("Not every capture was stored.");

            // Exact pixels back, and nothing readable in the file.
            // Compared with the very bitmap that was stored: drawing the sample again can smooth its
            // text a little differently, which is not a storage change.
            var first = CaptureLibrary.LoadImage(ids[0]);
            var original = firstStored!;
            bool exact = SamePixels(first, original);
            byte[] file;
            using (var f = new FileStream(CaptureLibrary.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { f.CopyTo(ms); file = ms.ToArray(); }
            bool plainHeader = Encoding.ASCII.GetString(file, 0, 16).StartsWith("SQLite format 3", StringComparison.Ordinal);
            bool titleInFile = ContainsAscii(file, "Contoso");
            log.AppendLine($"exact_pixels={exact} plain_header={plainHeader} title_visible_in_file={titleInFile} bytes={new FileInfo(CaptureLibrary.FilePath).Length}");
            if (!exact) throw new InvalidOperationException("A stored capture came back changed.");
            if (plainHeader || titleInFile) throw new InvalidOperationException("The library file is not encrypted.");

            // Search: titles, programs, notes and collection names.
            int byTitle = CaptureLibrary.Items("invoice").Count, byProcess = CaptureLibrary.Items("figma").Count;
            int byNote = CaptureLibrary.Items("spacing").Count, byCollection = CaptureLibrary.Items("receipts").Count, prefix = CaptureLibrary.Items("pipel").Count;
            log.AppendLine($"search invoice={byTitle} figma={byProcess} spacing={byNote} receipts={byCollection} pipel={prefix}");
            if (byTitle != 6 || byProcess != 6 || byNote != 1 || byCollection != 4 || prefix != 6) throw new InvalidOperationException("Search found the wrong captures.");

            // Text recognition runs in the background; the words inside the pictures become searchable.
            int ocr = 0;
            for (int t = 0; t < 120 && ocr == 0; t++)
            {
                await Task.Delay(500);
                ocr = CaptureLibrary.Items("crashed").Count;
            }
            log.AppendLine($"ocr_search crashed={ocr}");
            if (ocr == 0) log.AppendLine("ocr_note=no Windows text recognizer for the profile languages, or still running");

            // The Screenshots folder: five files shown as a live collection, and a sixth that
            // arrives while it is watched.
            string shots = Path.Combine(dir, "Screenshots");
            for (int n = 0; n < 5; n++)
            {
                string shotFile = Path.Combine(shots, $"Screenshot 2026-10-{10 + n} 0915{n:00}.png");
                ImageOutput.SavePng(Picture(70 + n, "Screenshot " + (n + 1), "Saved by Windows", Color.FromRgb(0x5E, 0x35, 0xB1)), shotFile);
                File.SetLastWriteTime(shotFile, now.AddDays(-n - 1));
            }
            int folderCount = FolderCaptures.Items().Count;
            var firstFile = FolderCaptures.Items()[0];
            bool folderPixels = SamePixels(CaptureLibrary.LoadImage(firstFile.Id), ImageOutput.Load(FolderCaptures.PathOf(firstFile.Id)!));
            bool folderThumb = CaptureLibrary.LoadThumb(firstFile.Id) != null;
            int folderSearch = FolderCaptures.Items("2026-10-12").Count;
            log.AppendLine($"folder count={folderCount} pixels={folderPixels} thumb={folderThumb} search={folderSearch}");
            if (folderCount != 5 || !folderPixels || !folderThumb || folderSearch != 1) throw new InvalidOperationException("The Screenshots folder collection is wrong.");

            // Window: each view, both groupings, a search and the loading screen.
            App.Instance.SetDocument(null);
            var w = new MainWindow { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1280, Height = 820 };
            w.Show();
            await Task.Delay(900);
            async Task Shot(string name)
            {
                await Task.Delay(900);
                w.UpdateLayout();
                var root = (FrameworkElement)w.Content;
                var dpi = VisualTreeHelper.GetDpi(w);
                var rtb = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                    96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                rtb.Render(root);
                ImageOutput.SavePng(rtb, Path.Combine(dir, name + ".png"));
                log.AppendLine($"shot {name} {rtb.PixelWidth}x{rtb.PixelHeight}");
            }
            var view = w.TestLibrary;
            foreach (var (v, g) in new[] { ("shelves", "collection"), ("shelves", "date"), ("flow", "date"), ("flow", "collection"), ("grid", "collection"), ("grid", "date"), ("timeline", "date") })
            {
                view.TestSet(v, g, "");
                await Shot($"library-{v}-{g}");
            }
            view.TestSet("grid", "date", "");
            FilmGrain.Set(false);
            await Shot("library-grid-grain-off");
            FilmGrain.Set(true);
            await Shot("library-grid-grain-on");
            view.TestSet("grid", "date", "mmc");
            await Shot("library-search");
            view.TestSet("shelves", "collection", "");
            view.TestShowBusy(true);
            await Shot("library-loading");
            view.TestShowBusy(false);
            foreach (var t in new[] { Theme.Light, Theme.Black, Theme.SE98 })
            {
                ThemeManager.Apply(t);
                await Shot("library-theme-" + t);
            }
            ThemeManager.Apply(Theme.Dark);
            ThemeManager.ApplyAccent(Theme.Dark, Accent.Teal);
            LocaleManager.Apply(Locale.De);
            w.TestRelocalize();
            await Shot("library-locale-de");
            LocaleManager.Apply(Locale.EnUS);
            w.TestRelocalize();

            // A file arriving in the watched folder shows up without a reload.
            bool changed = false;
            FolderCaptures.Changed += () => changed = true;
            ImageOutput.SavePng(Picture(80, "Screenshot 6", "Arrived later", Color.FromRgb(0x5E, 0x35, 0xB1)), Path.Combine(shots, "Screenshot 2026-10-17 120000.png"));
            for (int t = 0; t < 20 && !changed; t++) await Task.Delay(200);
            log.AppendLine($"folder_watch changed={changed} count={FolderCaptures.Items().Count}");
            if (!changed || FolderCaptures.Items().Count != 6) throw new InvalidOperationException("The Screenshots folder is not watched.");
            view.TestSet("shelves", "collection", "");
            await Shot("library-folder-collection");

            // The folder source must keep its files in the Screenshots group, not Unfiled.
            // Choose it in the collection sidebar, as a user does, so every view agrees on it.
            w.TestNavigationStrip!.SetSource("folder");
            view.TestSet("shelves", "collection", "");
            var rowsList = view.FindName("RowsList") as ItemsControl
                ?? throw new InvalidOperationException("Library rows are unavailable.");
            var folderRows = ((IEnumerable<LibraryRow>)rowsList.ItemsSource).ToList();
            bool folderGrouped = folderRows.Count == 1 && folderRows[0].Key == "folder"
                && folderRows[0].Title == FolderCaptures.Name && folderRows[0].Items.Count == 6;
            log.AppendLine($"folder_grouped={folderGrouped}");
            if (!folderGrouped) throw new InvalidOperationException("Folder captures were grouped under Unfiled.");
            await Shot("library-folder-source");
            view.TestSet("grid", "collection", "2026-10-12");
            folderRows = ((IEnumerable<LibraryRow>)rowsList.ItemsSource).ToList();
            if (folderRows.Count < 2 || folderRows[0].Key != "folder" || folderRows.Sum(r => r.Items.Count) != 1)
                throw new InvalidOperationException("Folder search lost its Screenshots group.");
            view.TestSet("shelves", "date", "");
            folderRows = ((IEnumerable<LibraryRow>)rowsList.ItemsSource).ToList();
            if (folderRows.Sum(r => r.Items.Count) != 6)
                throw new InvalidOperationException("Folder date grouping lost captures.");
            w.TestNavigationStrip!.SetSource("all");
            view.TestSet("shelves", "collection", "");

            // Open a capture, then the library over it (Ctrl+L).
            w.TestOpenLibraryCapture(CaptureLibrary.Items().First(c => c.Id == ids[3]));
            await Shot("editor-from-library");
            w.TestToggleLibrary();
            await Shot("library-over-image");
            w.TestToggleLibrary();

            // Collection navigation at either side and photos above or below the open picture.
            var dock = w.TestStripDock;
            log.AppendLine($"strip shown={dock.ShowPane} side={dock.Side}");
            if (!dock.ShowPane) throw new InvalidOperationException("The collection strip is not shown with a picture open.");
            foreach (var side in new[] { KillerFamily.Layout.DockSide.Top, KillerFamily.Layout.DockSide.Left, KillerFamily.Layout.DockSide.Right, KillerFamily.Layout.DockSide.Bottom })
            {
                var host = side is KillerFamily.Layout.DockSide.Left or KillerFamily.Layout.DockSide.Right ? w.TestNavigationDock : dock;
                host.DockTo(side);
                await Shot("strip-" + side.ToString().ToLowerInvariant());
                if (dock.IsHorizontal && w.TestStrip is { } strip)
                {
                    var flow = strip.TestCarousel.TransformToAncestor(strip).TransformBounds(new Rect(strip.TestCarousel.RenderSize));
                    log.AppendLine($"strip {side} carousel={flow}");
                    if (flow.Top > 8 || flow.Width < strip.ActualWidth - 24)
                        throw new InvalidOperationException($"The {side} carousel must use the strip without a caption row.");
                }
            }
            dock.Minimize();
            await Shot("strip-bottom-minimized");
            dock.Restore();
            dock.DockTo(KillerFamily.Layout.DockSide.Top);
            w.TestOpenLibraryCapture(FolderCaptures.Items()[1]);
            await Shot("strip-folder");
            log.AppendLine($"strip folder_doc={App.Instance.Document?.FilePath != null} current={App.Instance.CurrentItem?.Id}");
            App.Instance.SetDocument(null);
            var navigation = w.TestNavigationDock;
            log.AppendLine($"strip available_in_album={navigation.ShowPane} side={navigation.Side}");
            if (!navigation.ShowPane || navigation.IsHorizontal)
                throw new InvalidOperationException("The album lost its collection sidebar.");

            // Password: set, lock, refuse a wrong one, unlock, change, remove.
            CaptureLibrary.SetPassword("first pass");
            bool keyGone = !File.Exists(Path.Combine(lib, "Library.key"));
            CaptureLibrary.Close();
            bool autoRefused = !CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword;
            view.Reload();
            await Shot("library-locked");
            bool wrongRefused = !CaptureLibrary.Unlock("wrong");
            bool unlocked = CaptureLibrary.Unlock("first pass");
            bool check = CaptureLibrary.CheckPassword("first pass") && !CaptureLibrary.CheckPassword("First pass");
            int afterUnlock = CaptureLibrary.Count;
            CaptureLibrary.SetPassword("second pass");
            CaptureLibrary.Close();
            bool oldRefused = !CaptureLibrary.Unlock("first pass");
            bool newWorks = CaptureLibrary.Unlock("second pass");

            // A staged key from a stop before the swap must not replace the old password.
            string stagedKey = Path.Combine(lib, "Library.key.next");
            string unusedKey = "x'" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) + "'";
            File.WriteAllBytes(stagedKey, ProtectedData.Protect(Encoding.UTF8.GetBytes(unusedKey), entropy, DataProtectionScope.CurrentUser));
            CaptureLibrary.Close();
            bool beforeSwapNeedsPassword = !CaptureLibrary.OpenAuto() && CaptureLibrary.NeedsPassword;
            bool beforeSwapRecovered = CaptureLibrary.Unlock("second pass") && CaptureLibrary.Count == 38
                && SamePixels(CaptureLibrary.LoadImage(ids[0]), original) && !File.Exists(stagedKey);
            log.AppendLine($"password_before_swap={beforeSwapNeedsPassword && beforeSwapRecovered}");
            if (!(beforeSwapNeedsPassword && beforeSwapRecovered)) throw new InvalidOperationException("The old password album did not survive the staged key.");
            w.TestShowCaptureSettings();
            await Shot("capture-settings-password");
            w.TestCloseOverlays();
            CaptureLibrary.SetPassword(null);
            CaptureLibrary.Close();
            bool autoAgain = CaptureLibrary.OpenAuto();
            log.AppendLine($"password key_removed={keyGone} auto_refused={autoRefused} wrong_refused={wrongRefused} unlocked={unlocked} check={check} count={afterUnlock} old_refused={oldRefused} new_works={newWorks} removed_auto_opens={autoAgain}");
            if (!(keyGone && autoRefused && wrongRefused && unlocked && check && afterUnlock == 38 && oldRefused && newWorks && autoAgain))
                throw new InvalidOperationException("Password check failed.");

            // Simulate a stop after the new album replaces the old one but before its key moves.
            string activeKey = Path.Combine(lib, "Library.key");
            CaptureLibrary.Close();
            File.Move(activeKey, stagedKey);
            bool recoveredSwap = CaptureLibrary.OpenAuto() && CaptureLibrary.IsOpen
                && CaptureLibrary.Count == 38 && SamePixels(CaptureLibrary.LoadImage(ids[0]), original)
                && File.Exists(activeKey) && !File.Exists(stagedKey);
            log.AppendLine($"password_recovered_swap={recoveredSwap}");
            if (!recoveredSwap) throw new InvalidOperationException("The album could not recover its staged key.");

            // A backup from a stop after key promotion is cleared only after the album opens.
            CaptureLibrary.Close();
            string backup = CaptureLibrary.FilePath + ".bak";
            File.Copy(CaptureLibrary.FilePath, backup, true);
            bool recoveredPromotion = CaptureLibrary.OpenAuto() && CaptureLibrary.Count == 38
                && SamePixels(CaptureLibrary.LoadImage(ids[0]), original) && !File.Exists(backup);
            log.AppendLine($"password_recovered_promotion={recoveredPromotion}");
            if (!recoveredPromotion) throw new InvalidOperationException("The album did not validate after key promotion.");
            w.TestShowCaptureSettings();
            await Shot("capture-settings-account");
            w.TestCloseOverlays();

            // Locked captures wait in memory and are stored on unlock.
            CaptureLibrary.SetPassword("third pass");
            CaptureLibrary.Close();
            CaptureLibrary.OpenAuto();
            string blockedCs = new SqliteConnectionStringBuilder
            {
                DataSource = CaptureLibrary.FilePath, Password = "third pass"
            }.ConnectionString;
            using (var db = new SqliteConnection(blockedCs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "CREATE TRIGGER block_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'write blocked'); END";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            CaptureLibrary.Add(Picture(99, "While locked", "Pending capture", Colors.SlateGray), DateTime.Now, "region", "While locked", "notepad");
            await Task.Delay(500);
            int pending = CaptureLibrary.PendingCount;
            CaptureLibrary.Unlock("third pass");
            await Task.Delay(1500);
            bool writeRetained = CaptureLibrary.PendingCount == 1 && CaptureLibrary.Items("while locked").Count == 0;
            log.AppendLine($"pending_write_retained={writeRetained}");
            if (!writeRetained) throw new InvalidOperationException("A failed album write lost its pending capture.");
            using (var db = new SqliteConnection(blockedCs))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "DROP TRIGGER block_capture";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();
            CaptureLibrary.Close();
            CaptureLibrary.OpenAuto();
            CaptureLibrary.Unlock("third pass");
            int stored = 0;
            for (int t = 0; t < 40 && stored == 0; t++) { await Task.Delay(250); stored = CaptureLibrary.Items("while locked").Count; }
            log.AppendLine($"pending={pending} stored_after_retry={stored} remaining={CaptureLibrary.PendingCount}");
            if (pending != 1 || stored != 1 || CaptureLibrary.PendingCount != 0) throw new InvalidOperationException("A capture was lost or duplicated on retry.");
            CaptureLibrary.SetPassword(null);

            // History limit: the newest 30 stay.
            CaptureLibrary.Keep = "count:30";
            for (int t = 0; t < 40 && CaptureLibrary.Count != 30; t++) await Task.Delay(250);
            log.AppendLine($"keep_count_30={CaptureLibrary.Count} newest_kept={CaptureLibrary.Items().Any(c => c.Window == "While locked")}");
            if (CaptureLibrary.Count != 30) throw new InvalidOperationException("The history limit did not apply.");
            CaptureLibrary.Keep = "all";

            // Delete: a collection keeps its captures; a capture goes for good.
            CaptureLibrary.DeleteCollection(design);
            CaptureLibrary.Delete([ids[1]]);
            log.AppendLine($"after_delete count={CaptureLibrary.Count} collections={CaptureLibrary.Collections().Count}");
            if (CaptureLibrary.Count != 29 || CaptureLibrary.Collections().Count != 2) throw new InvalidOperationException("Delete removed the wrong things.");

            w.Close();
            CaptureLibrary.Close();
            log.AppendLine("result=ok");
        }
        catch (Exception ex)
        {
            log.AppendLine($"result=error {ex}");
            code = 1;
        }
        try { File.WriteAllText(Path.Combine(dir, "report.txt"), log.ToString()); } catch { }
        Application.Current.Shutdown(code);
    }

    /// <summary>A made-up window: title bar, side panel, content blocks and one line of text.</summary>
    private static BitmapSource Picture(int n, string title, string text, Color tint)
    {
        int w = n % 3 == 0 ? 1600 : n % 3 == 1 ? 1280 : 960, h = n % 3 == 0 ? 900 : n % 3 == 1 ? 800 : 720;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var dark = Color.FromRgb((byte)(tint.R / 5), (byte)(tint.G / 5), (byte)(tint.B / 5));
            dc.DrawRectangle(new LinearGradientBrush(dark, Color.FromRgb(0x14, 0x16, 0x1A), 60), null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(tint), null, new Rect(0, 0, w, 44));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, new Rect(0, 44, 220, h - 44));
            var face = new Typeface("Segoe UI");
            dc.DrawText(new FormattedText(title, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 20, Brushes.White, 1), new Point(16, 10));
            for (int r = 0; r < 4; r++)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(30 + r * 12), tint.R, tint.G, tint.B)), null,
                    new Rect(260, 220 + r * 120, w - 300 - (r * 37 + n * 11) % 260, 90), 8, 8);
            dc.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 44, Brushes.White, 1), new Point(260, 100));
            dc.DrawText(new FormattedText("#" + (n + 1), System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 28, Brushes.White, 1), new Point(24, h - 60));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static bool SamePixels(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight) return false;
        var ca = new FormatConvertedBitmap(a, PixelFormats.Bgra32, null, 0);
        var cb = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        int stride = a.PixelWidth * 4;
        var pa = new byte[stride * a.PixelHeight];
        var pb = new byte[stride * b.PixelHeight];
        ca.CopyPixels(pa, stride, 0);
        cb.CopyPixels(pb, stride, 0);
        return pa.AsSpan().SequenceEqual(pb);
    }

    private static bool ContainsAscii(byte[] data, string text) =>
        data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;
}
