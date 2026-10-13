using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace KillerShot.Services;

/// <summary>Typed access to the screen recording choices stored in <see cref="Settings"/>.</summary>
internal static class VideoSettings
{
    public static readonly int[] Mp4FpsChoices = [15, 24, 30, 60];
    public static readonly int[] GifFpsChoices = [5, 8, 10, 15, 20, 25];
    public static readonly int[] GifColorChoices = [256, 128, 64, 32];
    public static readonly int[] GifSizeChoices = [100, 75, 50];

    private static int GetInt(string key, int fallback, int[] allowed) =>
        int.TryParse(Settings.Get(key), out int v) && Array.IndexOf(allowed, v) >= 0 ? v : fallback;

    private static void SetInt(string key, int v) => Settings.Set(key, v.ToString(CultureInfo.InvariantCulture));

    /// <summary>"mp4" or "gif".</summary>
    public static string Format
    {
        get => Settings.Get("VideoFormat") == "gif" ? "gif" : "mp4";
        set => Settings.Set("VideoFormat", value == "gif" ? "gif" : "mp4");
    }

    public static int Mp4Fps { get => GetInt("VideoMp4Fps", 30, Mp4FpsChoices); set => SetInt("VideoMp4Fps", value); }

    /// <summary>0 small file, 1 balanced, 2 high quality.</summary>
    public static int Mp4Quality
    {
        get => int.TryParse(Settings.Get("VideoMp4Quality"), out int q) ? Math.Clamp(q, 0, 2) : 1;
        set => SetInt("VideoMp4Quality", Math.Clamp(value, 0, 2));
    }

    /// <summary>Bits per pixel per frame for the chosen quality.</summary>
    public static double Mp4BitsPerPixel => Mp4Quality switch { 0 => 0.04, 2 => 0.2, _ => 0.09 };

    public static int GifFps { get => GetInt("VideoGifFps", 10, GifFpsChoices); set => SetInt("VideoGifFps", value); }
    public static int GifColors { get => GetInt("VideoGifColors", 256, GifColorChoices); set => SetInt("VideoGifColors", value); }
    public static int GifSize { get => GetInt("VideoGifSize", 100, GifSizeChoices); set => SetInt("VideoGifSize", value); }

    /// <summary>Sound for MP4 recordings. GIF has no sound.</summary>
    public static KillerShot.Video.SoundSource Sound
    {
        get => Enum.TryParse<KillerShot.Video.SoundSource>(Settings.Get("VideoSound"), out var s) ? s : KillerShot.Video.SoundSource.None;
        set => Settings.Set("VideoSound", value.ToString());
    }

    public static readonly int[] AudioKbpsChoices = [96, 128, 160, 192];
    public static int AudioKbps { get => GetInt("VideoAudioKbps", 128, AudioKbpsChoices); set => SetInt("VideoAudioKbps", value); }

    public static bool GifLoop { get => Settings.GetBool("VideoGifLoop", true); set => Settings.SetBool("VideoGifLoop", value); }
    public static bool IncludeCursor { get => Settings.GetBool("VideoCursor", true); set => Settings.SetBool("VideoCursor", value); }
    public static bool Countdown { get => Settings.GetBool("VideoCountdown", true); set => Settings.SetBool("VideoCountdown", value); }
    public static bool CopyFile { get => Settings.GetBool("VideoCopyFile", false); set => Settings.SetBool("VideoCopyFile", value); }
    public static bool Notify { get => Settings.GetBool("VideoNotify", true); set => Settings.SetBool("VideoNotify", value); }

    public static string OutputFolder
    {
        get => Settings.Get("VideoFolder") is { Length: > 0 } f ? f : DefaultFolder;
        set => Settings.Set("VideoFolder", value);
    }

    public static string FileNamePattern
    {
        get => Settings.Get("VideoFileNamePattern") is { Length: > 0 } p ? p : CaptureSettings.DefaultFileNamePattern;
        set => Settings.Set("VideoFileNamePattern", value);
    }

    private static readonly Guid VideosFolderId = new("18989b1d-99b5-455b-841c-ab7c74e4ddfc");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Videos\Screen Recordings, the folder Windows' own recorder uses.</summary>
    public static string DefaultFolder
    {
        get
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            try
            {
                if (SHGetKnownFolderPath(VideosFolderId, 0, IntPtr.Zero, out IntPtr p) == 0)
                {
                    try { if (Marshal.PtrToStringUni(p) is { Length: > 0 } v) videos = v; }
                    finally { Marshal.FreeCoTaskMem(p); }
                }
            }
            catch { }
            return Path.Combine(videos, "Screen Recordings");
        }
    }
}
