using System.IO;

namespace KillerShot.Services;

internal sealed record RecordingStorageResult(long Id, long Session, Exception? CleanupError)
{
    public string? Warning(string sourcePath) => CleanupError == null ? null
        : string.Format(System.Globalization.CultureInfo.CurrentCulture,
            LocaleManager.Loc("Str_Rec_PlaintextRetained"), sourcePath, CleanupError.Message);
}

/// <summary>
/// Where finished recordings go. Nothing is chosen until the first recording, which asks once
/// (Controls/RecordingStorageDialog); the answer can be changed later in Video settings.
///
/// "folder" leaves each recording as a plain file in the recording folder, which the library
/// shows as its Recordings collection. "library" moves each one into the encrypted library's
/// Recordings collection and deletes the plain file, so the only copy is the encrypted one. A
/// recording the library cannot take (too large, locked, a failed write) stays in the folder.
/// </summary>
internal static class RecordingStorage
{
    public const string Folder = "folder";
    public const string Library = "library";

    /// <summary>"folder", "library", or null before the first recording asked.</summary>
    public static string? Choice
    {
        get => Settings.Get("RecordingStorage") is Folder or Library ? Settings.Get("RecordingStorage") : null;
        set => Settings.Set("RecordingStorage", value is Folder or Library ? value : "");
    }

    /// <summary>Moves a finished recording into the encrypted library and deletes the plain file
    /// once the copy is committed. Throws, leaving the file where it was, when the library is
    /// locked, the clip is over MaxVideoBytes, or the write fails. A committed copy and any
    /// failure to remove its original are returned together, so cleanup never retries the import.</summary>
    public static Task<RecordingStorageResult> MoveToLibraryAsync(string path, DateTime taken)
    {
        long session = CaptureLibrary.Session;
        return Task.Run(() =>
        {
            if (!CaptureLibrary.IsOpen) throw new InvalidOperationException(LocaleManager.Loc("Str_Rec_LibraryLocked"));
            long id = CaptureLibrary.AddVideo(path, taken, Path.GetFileNameWithoutExtension(path),
                expectedSession: session, recordingsCollection: true);
            Exception? cleanupError = null;
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { cleanupError = error; }
            return new RecordingStorageResult(id, session, cleanupError);
        });
    }
}
