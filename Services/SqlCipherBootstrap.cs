using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KillerShot.Services;

/// <summary>
/// Keeps the build self-sufficient for SQLCipher, as in KillerNotes and Killendar. The native
/// e_sqlcipher.dll (x64) is embedded as a resource and self-extracted to a per-version cache on
/// first use, then preloaded so the SQLitePCLRaw static provider's DllImport resolves to it. Must
/// run before SQLitePCL.raw.SetProvider - see the CaptureLibrary static constructor. Thread-safe.
/// </summary>
internal static class SqlCipherBootstrap
{
    private const string ResourceName = "KillerShot.SqlCipherNative.e_sqlcipher.dll";

    private static readonly object _gate = new();
    private static volatile bool _ready;
    private static string? _cacheRootForTests;

    internal static void UseCacheFolderForTests(string folder)
    {
        lock (_gate)
        {
            if (_ready) throw new InvalidOperationException("The native provider is already loaded.");
            _cacheRootForTests = Path.GetFullPath(folder);
        }
    }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    /// <summary>
    /// Extracts the embedded native when the cached copy differs and preloads it. Natives live
    /// in a per-version cache because they must match the app.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_ready) return;
        lock (_gate)
        {
            if (_ready) return;

            var asm = typeof(SqlCipherBootstrap).Assembly;
            string version = asm.GetName().Version?.ToString() ?? "0";
            string cacheRoot = _cacheRootForTests ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerShot", "native");
            string nativeDir = Path.Combine(cacheRoot, version);
            string target = Path.Combine(nativeDir, "e_sqlcipher.dll");

            using var src = asm.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The embedded SQLCipher native library is missing.");
            using var embedded = new MemoryStream();
            src.CopyTo(embedded);
            byte[] contents = embedded.ToArray();
            byte[] expectedHash = SHA256.HashData(contents);

            Directory.CreateDirectory(nativeDir);
            if (!CachedMatches(target, expectedHash))
            {
                // Each process writes its own temporary file. Rename within the same directory
                // publishes the complete image without a missing-target window for other readers.
                string temp = Path.Combine(nativeDir, $"e_sqlcipher.{Guid.NewGuid():N}.tmp");
                try
                {
                    using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        dst.Write(contents);
                        dst.Flush(true);
                    }
                    if (!CachedMatches(target, expectedHash))
                    {
                        try { File.Move(temp, target, true); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException &&
                            CachedMatches(target, expectedHash)) { }
                    }
                }
                finally
                {
                    try { File.Delete(temp); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }

            if (!CachedMatches(target, expectedHash))
                throw new IOException("The cached SQLCipher native library differs from the embedded copy.");
            if (!SetDllDirectory(nativeDir))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the SQLCipher native library path.");
            if (LoadLibrary(target) == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not load the SQLCipher native library.");
            _ready = true;
        }
    }

    private static bool CachedMatches(string target, byte[] expectedHash)
    {
        try
        {
            using var cached = new FileStream(target, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return SHA256.HashData(cached).SequenceEqual(expectedHash);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
