using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace KillerShot.Services;

/// <summary>
/// Screen recordings in the library. A recording is a capture whose mode is "video": in the
/// Recordings folder collection it is the MP4 or GIF file itself, and in the encrypted library
/// it is a row whose image is a poster frame and whose clip lives in capture_videos. Opening one
/// plays it in the viewer's own player instead of the editor.
///
/// Windows players cannot read the encrypted library, so a stored clip plays from a decrypted
/// copy in %LOCALAPPDATA%\KillerShot\Playback. Those copies are deleted when KillerShot closes
/// and again when it starts, in case it did not close cleanly.
/// </summary>
internal static class VideoCaptures
{
    public const string Mode = "video";
    private static readonly string[] Extensions = [".mp4", ".gif"];
    private static string? _playbackOverride;
    private static readonly object PlaybackGate = new();
    private static readonly Dictionary<(long Session, long Id, long Revision), string> PlaybackCopies = [];

    public static bool IsVideo(CaptureItem item) => item.Mode == Mode;

    public static bool IsVideoFile(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string PlaybackFolder => _playbackOverride ?? Path.Combine(CaptureLibrary.DefaultFolder, "Playback");

    /// <summary>For the self-test: another playback folder in place of the user's.</summary>
    internal static void UsePlaybackFolder(string dir) => _playbackOverride = dir;

    /// <summary>The frame size and length of a recording, from the properties Windows reads out
    /// of the file. A GIF's size comes from its first frame. Zero when Windows cannot tell.</summary>
    public static (int Width, int Height, TimeSpan Duration) Probe(string path)
    {
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0];
                return (frame.PixelWidth, frame.PixelHeight, TimeSpan.Zero);
            }
            catch { return (0, 0, TimeSpan.Zero); }
        }
        IPropertyStore? store = null;
        try
        {
            Guid iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store) != 0 || store == null) return (0, 0, TimeSpan.Zero);
            int width = (int)ReadUInt(store, FrameWidth);
            int height = (int)ReadUInt(store, FrameHeight);
            var duration = TimeSpan.FromTicks((long)Math.Min(ReadUInt(store, Duration), (ulong)TimeSpan.MaxValue.Ticks));
            return (width, height, duration);
        }
        catch { return (0, 0, TimeSpan.Zero); }
        finally { if (store != null) Marshal.ReleaseComObject(store); }
    }

    /// <summary>A poster frame no larger than longSide on its long side: the thumbnail Windows
    /// makes for an MP4, or a GIF's first frame. Null when Windows has no thumbnail for it.</summary>
    public static BitmapSource? Poster(string path, int longSide)
    {
        if (Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var (w, h, _) = Probe(path);
                if (w <= 0 || h <= 0) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (w >= h) bmp.DecodePixelWidth = Math.Min(longSide, w); else bmp.DecodePixelHeight = Math.Min(longSide, h);
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                }
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        IShellItemImageFactory? factory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
            if (factory.GetImage(new NativeSize { Width = longSide, Height = longSide }, SiigbfThumbnailOnly, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            // The shell hands back premultiplied pixels with no alpha worth keeping; a video frame is opaque.
            var opaque = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            var frozen = new WriteableBitmap(opaque);
            frozen.Freeze();
            return frozen;
        }
        catch { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Plays a recording in the viewer's default player. A folder clip plays as the file
    /// it is; a stored clip is decrypted to the playback folder first.</summary>
    public static void Play(CaptureItem item, Action<string>? status = null) => _ = PlayAsync(item, status);

    public static async Task PlayAsync(CaptureItem item, Action<string>? status = null, Action<string>? openPlayer = null)
    {
        try
        {
            string path = await Task.Run(() => PlayablePath(item));
            if (!CaptureLibrary.IsCurrent(item)) throw new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed"));
            (openPlayer ?? OpenInPlayer)(path);
        }
        catch (Exception ex) { status?.Invoke(ex.Message); }
    }

    /// <summary>The file a player can open for this recording, decrypting a stored clip.</summary>
    public static string PlayablePath(CaptureItem item)
    {
        if (FolderCaptures.IsFolderId(item.Id))
            return FolderCaptures.PathOf(item.Id) ?? throw new InvalidOperationException(LocaleManager.Loc("Str_Lib_Missing"));
        if (!CaptureLibrary.IsCurrent(item)) throw new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed"));
        return DecryptForPlayback(item.Id, item.Session);
    }

    /// <summary>Decrypts a stored clip into the playback folder and gives the copy's path.</summary>
    public static string DecryptForPlayback(long id) => DecryptForPlayback(id, null);

    /// <summary>Prepares a stored recording on a worker, keeping its original album identity.</summary>
    public static async Task<string> PreparePlaybackAsync(long id, long expectedSession)
    {
        string path = await Task.Run(() => DecryptForPlayback(id, expectedSession));
        if (CaptureLibrary.Session != expectedSession)
            throw new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed"));
        return path;
    }

    private static string DecryptForPlayback(long id, long? expectedSession)
    {
        lock (PlaybackGate)
        {
            var identity = CaptureLibrary.VideoPlaybackIdentity(id);
            if (expectedSession is long session && identity.Session != session)
                throw new InvalidOperationException(LocaleManager.Loc("Str_Album_Changed"));
            var key = (identity.Session, id, identity.Revision);
            if (PlaybackCopies.TryGetValue(key, out string? cached) && File.Exists(cached)) return cached;
            Directory.CreateDirectory(PlaybackFolder);
            string target = Path.Combine(PlaybackFolder, "KillerShot recording " + Guid.NewGuid().ToString("N") + identity.Extension);
            CaptureLibrary.ExportVideo(id, target, identity.Session);
            PlaybackCopies[key] = target;
            return target;
        }
    }

    /// <summary>Opens a file in the viewer's default player.</summary>
    public static void OpenInPlayer(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Deletes the decrypted playback copies of one album session, or of one recording in
    /// it. A copy a player still holds open stays until the next wipe.</summary>
    public static void ForgetPlayback(long session, long? id = null)
    {
        lock (PlaybackGate)
        {
            foreach (var key in PlaybackCopies.Keys.Where(k => k.Session == session && (id == null || k.Id == id)).ToList())
            {
                try
                {
                    File.Delete(PlaybackCopies[key]);
                    PlaybackCopies.Remove(key);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Deletes every decrypted playback copy. A copy a player still holds open stays
    /// until the next start.</summary>
    public static void WipePlayback()
    {
        lock (PlaybackGate)
        {
            PlaybackCopies.Clear();
            try
            {
                if (!Directory.Exists(PlaybackFolder)) return;
                foreach (string file in Directory.EnumerateFiles(PlaybackFolder))
                    try { File.Delete(file); } catch { }
            }
            catch { }
        }
    }

    /// <summary>Text for a clip's length, like 0:12 or 1:02:03.</summary>
    public static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}" : duration.ToString(@"m\:ss");

    // ============================================================
    // Shell interop
    // ============================================================

    private const int SiigbfThumbnailOnly = 0x08;

    private static readonly PropertyKey FrameWidth = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PropertyKey FrameHeight = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PropertyKey Duration = new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);

    private static ulong ReadUInt(IPropertyStore store, PropertyKey key)
    {
        var value = new PropVariant();
        try
        {
            if (store.GetValue(ref key, out value) != 0) return 0;
            return value.Type switch { 19 => value.UInt32, 21 => value.UInt64, _ => 0 };
        }
        finally { PropVariantClear(ref value); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize { public int Width; public int Height; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid format, uint id)
    {
        public readonly Guid Format = format;
        public readonly uint Id = id;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt32;
        [FieldOffset(8)] public ulong UInt64;
        [FieldOffset(8)] private IntPtr _pointer;
        [FieldOffset(16)] private IntPtr _pointer2;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, out IShellItemImageFactory factory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, int flags, ref Guid iid, out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
