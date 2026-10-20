using System.IO;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestAlbums
{
    private static void CheckPasswordCleanup(string original, List<string> report)
    {
        string keyPath = Path.ChangeExtension(original, ".key");
        CaptureLibrary.Close();
        byte[] oldDatabase = File.ReadAllBytes(original);
        byte[] oldKey = File.ReadAllBytes(keyPath);
        Check(CaptureLibrary.OpenAuto(), "Account fixture must reopen.");
        CaptureLibrary.SetPassword("cleanup fixture password");
        string work = CaptureLibrary.CreateAlbum("Other");
        Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Other album must open.");
        string[] artifacts = [keyPath, keyPath + ".next", original + ".bak"];
        void RestoreArtifacts()
        {
            File.WriteAllBytes(keyPath, oldKey);
            File.WriteAllBytes(keyPath + ".next", oldKey);
            File.WriteAllBytes(original + ".bak", oldDatabase);
        }
        void CheckRemoved() => Check(artifacts.All(path => !File.Exists(path)),
            "Password opening must remove obsolete account keys and the account-readable backup.");
        void CheckCapture()
        {
            Check(CaptureLibrary.Count == 1, "Password recovery must preserve capture count.");
            var image = CaptureLibrary.LoadImage(CaptureLibrary.Items().Single());
            var pixels = new byte[16];
            image.CopyPixels(pixels, 8, 0);
            Check(pixels[0] == 0x66 && pixels[3] == 255, "Password recovery must preserve exact capture pixels.");
        }
        RestoreArtifacts();
        Check(!CaptureLibrary.TrySwitchAlbum(original, "wrong password", out _) && artifacts.All(File.Exists)
            && CaptureLibrary.FilePath == work, "Wrong password must preserve the active album and recovery artifacts.");
        Check(CaptureLibrary.TrySwitchAlbum(original, "cleanup fixture password", out _), "Password album must open.");
        CheckRemoved();
        CheckCapture();
        foreach (string blockedPath in artifacts)
        {
            Check(CaptureLibrary.TrySwitchAlbum(work, null, out _), "Other album must reopen.");
            RestoreArtifacts();
            bool rejected = false;
            using (var held = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { CaptureLibrary.TrySwitchAlbum(original, "cleanup fixture password", out _); }
                catch (IOException) { rejected = true; }
                Check(rejected && CaptureLibrary.FilePath == work && CaptureLibrary.IsOpen,
                    "Failed cleanup must surface and preserve the current album.");
            }
            Check(CaptureLibrary.TrySwitchAlbum(original, "cleanup fixture password", out _), "Cleanup retry must succeed.");
            CheckRemoved();
            CheckCapture();
        }
        CaptureLibrary.Close();
        RestoreArtifacts();
        Check(!CaptureLibrary.OpenAuto(), "Stale account keys must not open the password album.");
        Check(!CaptureLibrary.Unlock("wrong password") && artifacts.All(File.Exists), "Wrong Unlock must preserve recovery artifacts.");
        using (var held = new FileStream(original + ".bak", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            bool rejected = false;
            try { CaptureLibrary.Unlock("cleanup fixture password"); }
            catch (IOException) { rejected = true; }
            Check(rejected && !CaptureLibrary.IsOpen, "Unlock cleanup failure must not publish an open album.");
        }
        Check(CaptureLibrary.Unlock("cleanup fixture password"), "Unlock retry must succeed.");
        CheckRemoved();
        CheckCapture();
        report.Add("Password switch and Unlock remove stale keys/backups, reject wrong passwords and retry cleanup failures: PASS");
    }
}
