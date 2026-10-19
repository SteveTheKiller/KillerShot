using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestAlbums
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] != "--selftest-albums") return false;
        _ = RunAsync(Path.GetFullPath(args[1]), args[2]);
        return true;
    }

    private static async Task RunAsync(string folder, string scenario)
    {
        var report = new List<string>();
        int exit = 1;
        try
        {
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            App.InitializeKit();
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            CaptureLibrary.AddNow(Picture(0x66), DateTime.Now, "region", "Original album", null);
            string original = CaptureLibrary.FilePath;
            if (scenario == "create")
            {
                string created = CaptureLibrary.CreateAlbum("Work");
                Check(File.Exists(created) && new FileInfo(created).Length > 0, "Create must initialize an encrypted album.");
                Check(File.Exists(Path.ChangeExtension(created, ".key")), "Create must preserve a matching account key.");
                Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1, "Creating must preserve the current album and its captures.");
                byte[] before = File.ReadAllBytes(created);
                bool rejected = false;
                try { CaptureLibrary.CreateAlbum("Work"); } catch (IOException) { rejected = true; }
                Check(rejected && before.SequenceEqual(File.ReadAllBytes(created)), "Creating a duplicate name must not overwrite its album.");
                rejected = false;
                try { CaptureLibrary.CreateAlbum("../Escape"); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "An album name must not escape its managed folder.");
                report.Add("Create initializes a separate album/key pair, preserves the current album, and rejects overwrite/path traversal: PASS");
            }
            else if (scenario == "switch")
            {
                string work = CaptureLibrary.CreateAlbum("Work");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "A new account-protected album must open.");
                Check(CaptureLibrary.FilePath == work && CaptureLibrary.Count == 0, "Switch must select the destination album.");
                CaptureLibrary.AddNow(Picture(0x99), DateTime.Now, "region", "Work album", null);
                bool rejected = false;
                try { CaptureLibrary.TrySwitchAlbum(Path.Combine(Path.GetDirectoryName(work)!, "Missing.kshot"), null, out _); }
                catch (FileNotFoundException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == work && CaptureLibrary.Items().Single().Window == "Work album",
                    "A missing destination must leave the current album open and unchanged.");
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _) && CaptureLibrary.Items().Single().Window == "Original album",
                    "Returning to the original album must preserve its data and account key.");
                CaptureLibrary.SetPassword("original password");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "The second album must retain its independent account key.");
                Check(!CaptureLibrary.TrySwitchAlbum(original, null, out bool needsPassword) && needsPassword,
                    "A protected destination must request a password.");
                Check(!CaptureLibrary.TrySwitchAlbum(original, "wrong password", out needsPassword) && needsPassword,
                    "A wrong password must be rejected.");
                Check(CaptureLibrary.FilePath == work && CaptureLibrary.IsOpen && CaptureLibrary.Count == 1,
                    "Missing and wrong destination passwords must leave the current album live.");
                Check(CaptureLibrary.TrySwitchAlbum(original, "original password", out _) && CaptureLibrary.Count == 1,
                    "The correct password must activate the protected destination.");
                report.Add("Switch preserves independent captures/keys and leaves the current album live after missing files and wrong passwords: PASS");
            }
            else if (scenario == "policies")
            {
                CaptureLibrary.Keep = "count:100";
                CaptureLibrary.QrReadingEnabled = true;
                long originalCollection = CaptureLibrary.CreateCollection("Original collection");
                CardColors.SetCollection(originalCollection, Colors.Red);
                string work = CaptureLibrary.CreateAlbum("Work");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Work must open.");
                Check(CaptureLibrary.Keep == "all" && !CaptureLibrary.QrReadingEnabled,
                    "A new album must have independent retention and QR choices.");
                long newCollection = CaptureLibrary.CreateCollection("New collection");
                Check(newCollection == originalCollection && CardColors.Collection(newCollection) == null,
                    "Equal collection IDs in another album must not inherit colors.");
                CaptureLibrary.Keep = "days:30";
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _), "Original must reopen.");
                Check(CaptureLibrary.Keep == "count:100" && CaptureLibrary.QrReadingEnabled,
                    "Returning to an album must restore its own retention and QR choices.");
                Check(CardColors.Collection(originalCollection) == Colors.Red, "Collection color must stay with its album.");
                CaptureLibrary.Close();
                CaptureLibrary.QrReadingEnabled = false;
                Check(!CaptureLibrary.QrReadingEnabled && CaptureLibrary.OpenAuto() && !CaptureLibrary.QrReadingEnabled,
                    "An editor reading choice made before open must apply only to that album when it opens.");
                report.Add("Retention and QR choices stay with each album: PASS");
            }
            else if (scenario == "settings-failure")
            {
                string work = CaptureLibrary.CreateAlbum("Work");
                Settings.SetRequired("ActiveAlbum", "Library.kshot");
                bool rejected = false;
                using (var blocked = new FileStream(Path.Combine(folder, "settings", "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                    try { CaptureLibrary.TrySwitchAlbum(work, null, out _); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1
                    && Settings.Get("ActiveAlbum") == "Library.kshot", "A failed settings replacement must leave the original connection and startup selection intact.");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Switch must be retryable after the file lock clears.");
                report.Add("A real settings file lock preserves the current album and startup selection; retry succeeds: PASS");
            }
            else if (scenario == "session")
            {
                var stale = CaptureLibrary.Items().Single();
                string work = CaptureLibrary.CreateAlbum("Work");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Work must open.");
                long current = CaptureLibrary.AddNow(Picture(0x99), DateTime.Now, "region", "Work album", null);
                Check(current == stale.Id, "The regression needs colliding numeric IDs in different albums.");
                await Task.Run(() => CaptureLibrary.StoreOcr(stale, "stale-result"));
                await Task.Run(() => CaptureLibrary.StoreQr(stale, [new KillerTools.Qr.QrResult("stale-qr", 1, KillerTools.Qr.QrErrorCorrection.L, [])]));
                Check(CaptureLibrary.Items("stale").Count == 0 && CaptureLibrary.QrCodes(current).Count == 0,
                    "Delayed OCR and QR results must not change an equal ID in the new album.");
                Check(CaptureLibrary.LoadThumb(stale) == null, "A stale thumbnail request must not read the new album.");
                bool rejected = false;
                try { CaptureLibrary.LoadImage(stale); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "A stale capture selection must not open a different album's image.");
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _) && !CaptureLibrary.IsCurrent(stale),
                    "Returning to the same album must not revive a stale session.");
                report.Add("Delayed OCR, QR, thumbnail and image selections cannot cross album sessions, including equal numeric IDs: PASS");
            }
            else if (scenario == "rename")
            {
                string renamed = CaptureLibrary.RenameAlbum(original, "Personal");
                Check(CaptureLibrary.FilePath == renamed && CaptureLibrary.IsOpen && CaptureLibrary.Count == 1,
                    "Renaming the active album must preserve its open captures.");
                Check(!File.Exists(original) && File.Exists(Path.ChangeExtension(renamed, ".key")), "Rename must move the matching key with the album.");
                CaptureLibrary.Close();
                Check(CaptureLibrary.OpenAuto() && CaptureLibrary.Count == 1, "The renamed album must reopen with its account key.");
                string blockedTarget = Path.Combine(Path.GetDirectoryName(renamed)!, "Blocked.kshot");
                bool rejected = false;
                using (var blocked = new FileStream(renamed, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    try { CaptureLibrary.RenameAlbum(renamed, "Blocked"); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == renamed && CaptureLibrary.IsOpen && CaptureLibrary.Count == 1
                    && !File.Exists(blockedTarget) && !File.Exists(Path.ChangeExtension(blockedTarget, ".key")),
                    "A real album rename lock must restore the original connection and clean only the attempted copy.");
                report.Add("Active rename preserves data/key and restart; locked native file failure restores the original album: PASS");
            }
            else if (scenario == "import")
            {
                string external = Path.Combine(folder, "external.kshot");
                CaptureLibrary.SetPassword("copy password");
                CaptureLibrary.Close();
                File.Copy(original, external);
                Check(CaptureLibrary.Unlock("copy password"), "Original must reopen.");
                byte[] before = File.ReadAllBytes(external);
                Check(CaptureLibrary.ImportAlbum(external, "wrong", out bool needsPassword) == null && needsPassword,
                    "Wrong import passwords must not promote a destination.");
                string imported = CaptureLibrary.ImportAlbum(external, "copy password", out _) ?? throw new InvalidOperationException("Import failed.");
                Check(imported != external && File.Exists(imported) && CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1,
                    "Import must create a managed validated copy while preserving the current album.");
                Check(before.SequenceEqual(File.ReadAllBytes(external)), "Import must not mutate the source album.");
                Check(CaptureLibrary.TrySwitchAlbum(imported, "copy password", out _) && CaptureLibrary.Count == 1,
                    "The copied album must be independently readable.");
                report.Add("Wrong-password imports preserve the current album; a validated managed copy preserves source bytes and captures: PASS");
            }
            else if (scenario == "recovery")
            {
                string work = CaptureLibrary.CreateAlbum("Work");
                string directory = Path.GetDirectoryName(original)!;
                string reserved = Path.Combine(directory, "Reserved.kshot");
                File.Copy(work, reserved + ".bak");
                File.Copy(Path.ChangeExtension(work, ".key"), Path.ChangeExtension(reserved, ".key.next"));
                bool rejected = false;
                try { CaptureLibrary.CreateAlbum("Reserved"); } catch (IOException) { rejected = true; }
                Check(rejected && !File.Exists(reserved), "A recovery-only album name must not be reused.");
                Check(CaptureLibrary.TrySwitchAlbum(reserved, null, out _) && CaptureLibrary.Count == 0,
                    "A managed destination must recover its backup and staged account key before activation.");
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _), "Original must reopen.");
                string renamed = CaptureLibrary.RenameAlbum(work, "Renamed");
                string reused = CaptureLibrary.CreateAlbum("Work");
                byte[] reusedKey = File.ReadAllBytes(Path.ChangeExtension(reused, ".key"));
                string journal = Path.Combine(directory, ".album-rename-fixture.json");
                File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new { OldName = "Work.kshot", NewName = "Renamed.kshot", Active = false, Sidecars = new[] { ".key" }, Committed = true }));
                CaptureLibrary.Close();
                Check(CaptureLibrary.OpenAuto(), "Recovery must open the original album.");
                Check(reusedKey.SequenceEqual(File.ReadAllBytes(Path.ChangeExtension(reused, ".key"))),
                    "A completed journal must never delete a new album's key at a reused old name.");
                Check(CaptureLibrary.TrySwitchAlbum(reused, null, out _), "The reused-name album must remain readable.");
                string inactive = CaptureLibrary.CreateAlbum("Inactive");
                string completed = Path.Combine(directory, "Finished.kshot");
                File.Copy(Path.ChangeExtension(inactive, ".key"), Path.ChangeExtension(completed, ".key"));
                File.Move(inactive, completed);
                File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new { OldName = "Inactive.kshot", NewName = "Finished.kshot", Active = false, Sidecars = new[] { ".key" }, Committed = false }));
                CaptureLibrary.Close();
                Check(CaptureLibrary.OpenAuto() && File.Exists(completed) && !File.Exists(inactive)
                    && !File.Exists(Path.ChangeExtension(inactive, ".key")) && CaptureLibrary.TrySwitchAlbum(completed, null, out _),
                    "An inactive rename must finish after its atomic move even if its cleanup marker was not saved.");
                report.Add("Recovery-only names stay reserved; backup/staged-key activation and completed-journal reuse preserve every album key: PASS");
            }
            else if (scenario == "cancel")
            {
                var document = new KillerShot.Editor.EditorDocument(Picture(0x44));
                App.Instance.SetDocument(document);
                string work = CaptureLibrary.CreateAlbum("Work");
                bool guarded = false;
                Check(!CaptureLibrary.TrySwitchAlbum(work, null, out bool needsPassword, () => { guarded = true; return false; }) && !needsPassword && guarded,
                    "A canceled unsaved-document guard must cancel a validated switch.");
                Check(CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1 && ReferenceEquals(App.Instance.Document, document),
                    "Cancel must preserve the current album and editor.");
                report.Add("Cancel after destination validation preserves the active album and editor: PASS");
            }
            else if (scenario == "guard-capture")
            {
                string work = CaptureLibrary.CreateAlbum("Work");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _, () =>
                {
                    CaptureLibrary.Add(Picture(0x77), DateTime.Now, "region", "Captured during confirmation", null);
                    return true;
                }), "The switch should finish after committing the old album's queued capture.");
                await CaptureLibrary.RetryPendingAsync();
                Check(CaptureLibrary.Count == 0, "A capture queued during confirmation must not land in the destination album.");
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _) && CaptureLibrary.Count == 2,
                    "The original album must retain the capture queued during confirmation.");
                report.Add("Nested confirmation captures commit to the original album before switching: PASS");
            }
            else if (scenario == "cli-recovery")
            {
                CaptureLibrary.SetPassword("recovery password");
                CaptureLibrary.Close();
                string moved = Path.Combine(Path.GetDirectoryName(original)!, "Personal.kshot");
                File.Move(original, moved);
                string journal = Path.Combine(Path.GetDirectoryName(original)!, ".album-rename-fixture.json");
                File.WriteAllText(journal, System.Text.Json.JsonSerializer.Serialize(new { OldName = "Library.kshot", NewName = "Personal.kshot", Active = true, Sidecars = Array.Empty<string>(), Committed = false }));
                Check(!CaptureLibrary.TryAddNowFromCli(Picture(0x33), DateTime.Now, "region", "CLI", null, out _)
                    && CaptureLibrary.NeedsPassword && File.Exists(original) && !File.Exists(Path.ChangeExtension(original, ".key")),
                    "CLI-first startup must recover the protected original instead of creating an empty replacement.");
                Check(CaptureLibrary.Unlock("recovery password") && CaptureLibrary.Count == 1,
                    "CLI recovery must retain the protected original captures.");
                report.Add("CLI-first startup recovers interrupted protected-album rename without creating a replacement: PASS");
            }
            else if (scenario == "pending")
            {
                string work = CaptureLibrary.CreateAlbum("Work");
                using var fault = OpenAccountFixture(original);
                void Sql(string statement) { using var command = fault.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery(); }
                Sql("CREATE TRIGGER fail_capture BEFORE INSERT ON captures BEGIN SELECT RAISE(ABORT, 'synthetic album transition failure'); END");
                CaptureLibrary.Add(Picture(0x88), DateTime.Now, "region", "Pending old capture", null);
                await CaptureLibrary.RetryPendingAsync();
                Check(CaptureLibrary.PendingCount == 1, "The synthetic write failure must retain the capture.");
                bool rejected = false;
                try { CaptureLibrary.TrySwitchAlbum(work, null, out _); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == original && CaptureLibrary.Count == 1 && CaptureLibrary.PendingCount == 1,
                    "Switch must leave a failed pending capture with its original album.");
                rejected = false;
                try { CaptureLibrary.RenameAlbum(original, "Blocked"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == original, "Rename must also preserve failed pending captures.");
                Sql("DROP TRIGGER fail_capture");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _) && CaptureLibrary.Count == 0 && CaptureLibrary.PendingCount == 0,
                    "A repaired pending capture must commit before switching.");
                Check(CaptureLibrary.TrySwitchAlbum(original, null, out _) && CaptureLibrary.Count == 2
                    && CaptureLibrary.Items().Count(item => item.Window == "Pending old capture") == 1,
                    "Retry must save the pending capture once in the original album.");
                report.Add("Failed writes block switch/rename; repaired captures commit exactly once to the original album before transition: PASS");
            }
            else if (scenario == "account-import")
            {
                string external = Path.Combine(folder, "external.kshot");
                CaptureLibrary.Close();
                File.Copy(original, external);
                File.Copy(Path.ChangeExtension(original, ".key"), Path.ChangeExtension(external, ".key"));
                Check(CaptureLibrary.OpenAuto(), "Original must reopen.");
                byte[] source = File.ReadAllBytes(external);
                string imported = CaptureLibrary.ImportAlbum(external, null, out _) ?? throw new InvalidOperationException("Account import failed.");
                Check(File.Exists(Path.ChangeExtension(imported, ".key")) && source.SequenceEqual(File.ReadAllBytes(external)),
                    "Account import must preserve source bytes and provide the copy's matching sealed key.");
                Check(CaptureLibrary.TrySwitchAlbum(imported, null, out _) && CaptureLibrary.Count == 1,
                    "The account-protected copy must open independently.");
                string invalid = Path.Combine(folder, "invalid.kshot");
                File.WriteAllBytes(invalid, []);
                File.Copy(Path.ChangeExtension(external, ".key"), Path.ChangeExtension(invalid, ".key"));
                int albums = CaptureLibrary.Albums().Count;
                bool rejected = false;
                try { CaptureLibrary.ImportAlbum(invalid, null, out _); } catch (InvalidDataException) { rejected = true; }
                Check(rejected && CaptureLibrary.Albums().Count == albums && CaptureLibrary.FilePath == imported && CaptureLibrary.Count == 1,
                    "Invalid copies must not be promoted or disturb the current album.");
                report.Add("Account-key import preserves source and opens its managed copy; incomplete source fails without promotion: PASS");
            }
            else if (scenario == "observer")
            {
                CaptureLibrary.BackgroundWork = true;
                CaptureLibrary.Close();
                Check(CaptureLibrary.OpenAuto(), "Original observer must open.");
                string work = CaptureLibrary.CreateAlbum("Work");
                Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Destination observer must open.");
                await Task.Delay(200);
                int changes = 0;
                void Changed() => Interlocked.Increment(ref changes);
                CaptureLibrary.Changed += Changed;
                try
                {
                    string childSettings = Path.Combine(folder, "child-settings");
                    Directory.CreateDirectory(childSettings);
                    File.WriteAllText(Path.Combine(childSettings, "settings.json"), "{\"ActiveAlbum\":\"Work.kshot\"}");
                    string image = Path.Combine(folder, "synthetic.png");
                    ImageOutput.SavePng(Picture(0x55), image);
                    var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
                    { UseShellExecute = false, CreateNoWindow = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden };
                    start.ArgumentList.Add("--selftest-external-album-child");
                    start.ArgumentList.Add(folder);
                    start.ArgumentList.Add(image);
                    using var child = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Could not start observer fixture.");
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    Check(child.ExitCode == 0, "The child must commit to the newly selected album.");
                    for (int attempt = 0; attempt < 100 && Volatile.Read(ref changes) == 0; attempt++) await Task.Delay(20);
                    Check(Volatile.Read(ref changes) > 0 && CaptureLibrary.FilePath == work && CaptureLibrary.Items().Single().Window == "External capture",
                        "Switching must observe cross-process changes on the destination path.");
                    Check(CaptureLibrary.TrySwitchAlbum(original, null, out _) && CaptureLibrary.Items().Single().Window == "Original album",
                        "Cross-process writes after a switch must leave the original album unchanged.");
                }
                finally { CaptureLibrary.Changed -= Changed; }
                report.Add("After switching, a real child process wakes the destination album observer and leaves the original unchanged: PASS");
            }
            else if (scenario == "manager") await CheckManagerAsync(folder, report);
            else if (scenario == "interaction") await CheckManagerInteractionAsync(folder, report);
            else throw new ArgumentException("Unknown album scenario.");
            await Task.CompletedTask;
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally { CaptureLibrary.Close(); }
        report.Add(exit == 0 ? "result=ok" : "result=error");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        App.Instance.Shutdown(exit);
    }

    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenAccountFixture(string path)
    {
        string key = System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
            File.ReadAllBytes(Path.ChangeExtension(path, ".key")), "KillerShot.Library.v1"u8.ToArray(),
            System.Security.Cryptography.DataProtectionScope.CurrentUser));
        var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = path, Password = key, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static BitmapSource Picture(byte value)
    {
        var image = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { value, 0, 0, 255, value, 0, 0, 255, value, 0, 0, 255, value, 0, 0, 255 }, 8);
        image.Freeze();
        return image;
    }
}
