using System.Windows.Media.Imaging;
using KillerShot.Editor;
using Microsoft.Data.Sqlite;

namespace KillerShot.Services;

/// <summary>Editable marks kept with the original inside the encrypted album. The capture's own
/// pixels never change; its marks, and the cropped picture when it was cropped, sit beside it.
/// Everything that leaves the album (copy, print, save, drag) is the flattened result.</summary>
internal static partial class CaptureLibrary
{
    private const string MarksSchema = """
        CREATE TABLE IF NOT EXISTS capture_marks(
            capture_id INTEGER PRIMARY KEY REFERENCES captures(id) ON DELETE CASCADE,
            version INTEGER NOT NULL,
            base_image BLOB,
            marks TEXT NOT NULL,
            output_image BLOB NOT NULL);
        """;

    /// <summary>A capture finished its album write. New captures open in the editor before the
    /// album has given them an id; the editor links its marks to them through this.</summary>
    public static event Action<BitmapSource, long>? CaptureStored;

    private sealed record StoredCapture(long Id, long Session);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, StoredCapture> StoredCaptures = new();

    internal static bool TryGetStoredCapture(BitmapSource image, long session, out long id)
    {
        lock (_gate)
        {
            if (_session == session && StoredCaptures.TryGetValue(image, out var stored) && stored.Session == session)
            {
                id = stored.Id;
                return true;
            }
            id = 0;
            return false;
        }
    }

    private static void RaiseCaptureStored(BitmapSource bmp, long id)
    {
        try { CaptureStored?.Invoke(bmp, id); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Capture link failed: {0}", error.Message); }
    }

    /// <summary>Stores the editor's current marks and base picture for a capture, or forgets them
    /// when the picture is back to the plain original, and shows the flattened result as its
    /// thumbnail. The caller renders <paramref name="flattened"/> on the interface thread.</summary>
    public static bool SaveMarks(long id, EditorState state, BitmapSource original, BitmapSource flattened, long? expectedSession = null)
    {
        long session = expectedSession ?? Session;
        bool plain = state.Items.IsEmpty && ReferenceEquals(state.Image, original);
        byte[]? baseImage = ReferenceEquals(state.Image, original) ? null : ImageOutput.EncodePng(state.Image);
        string marks = MarksFormat.Write(state.Items);
        byte[] thumb = EncodeThumb(plain ? original : flattened);
        byte[]? output = plain ? null : ImageOutput.EncodePng(flattened);
        lock (_gate)
        {
            if (_db == null || _session != session) return false;
            using var transaction = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = transaction;
                if (plain)
                {
                    cmd.CommandText = "DELETE FROM capture_marks WHERE capture_id = $id";
                }
                else
                {
                    cmd.CommandText = """
                        INSERT INTO capture_marks(capture_id, version, base_image, marks, output_image) VALUES ($id, $v, $base, $marks, $output)
                        ON CONFLICT(capture_id) DO UPDATE SET version = excluded.version, base_image = excluded.base_image,
                            marks = excluded.marks, output_image = excluded.output_image;
                        """;
                    cmd.Parameters.AddWithValue("$v", MarksFormat.Version);
                    cmd.Parameters.AddWithValue("$base", (object?)baseImage ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$marks", marks);
                    cmd.Parameters.AddWithValue("$output", output!);
                }
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = transaction;
                // Text and codes read from the picture before this edit may be under a redaction now:
                // forget them, so search and the QR marker follow the edited result once it is read again.
                cmd.CommandText = """
                    UPDATE captures SET thumb = $thumb, ocr_text = NULL WHERE id = $id;
                    DELETE FROM capture_qr WHERE capture_id = $id;
                    INSERT INTO capture_revisions(capture_id, revision) SELECT $id, 1 WHERE EXISTS(SELECT 1 FROM captures WHERE id=$id)
                    ON CONFLICT(capture_id) DO UPDATE SET revision=revision+1;
                    """;
                cmd.Parameters.AddWithValue("$thumb", thumb);
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteNonQuery() == 0) return false;   // deleted meanwhile; roll back
            }
            IndexText(id, transaction);
            transaction.Commit();
            lock (_ocrFailed) _ocrFailed.Remove(id);
            ClearThumbCache();
        }
        RaiseChanged();
        if (BackgroundWork)
        {
            StartOcr();
            StartQrReading();
        }
        return true;
    }

    /// <summary>Persisted with marks in the same transaction, including changes from another process.</summary>
    private static long MarksRevision(long id, SqliteTransaction? transaction = null)
    {
        using var command = Db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM capture_revisions WHERE capture_id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is long revision ? revision : 0;
    }

    internal enum MarksStatus { Absent, Editable, Unreadable }
    internal sealed record MarksRead(MarksStatus Status, EditorState? State);

    /// <summary>Loads one album identity atomically. Unreadable edits expose only flattened pixels
    /// and never attach the recovery document to the preserved edit metadata.</summary>
    public static EditorDocument LoadDocument(CaptureItem item)
    {
        lock (_gate)
        {
            if (!IsCurrent(item)) throw new InvalidOperationException(Loc("Str_Album_Changed"));
            var original = LoadImage(item);
            var marks = ReadMarks(item.Id, original);
            if (marks.Status == MarksStatus.Unreadable)
            {
                var output = EditedPng(item.Id) ?? throw new System.IO.InvalidDataException(Loc("Str_Marks_Unreadable"));
                try { return new EditorDocument(Decode(output, exactPixels: true), null, item.Taken) { Recovery = AlbumRecovery.Flattened }; }
                catch (Exception error) when (IsMarksReadError(error))
                { throw new System.IO.InvalidDataException(Loc("Str_Marks_Unreadable"), error); }
            }
            return marks.State is { } restored ? EditorDocument.Restore(original, restored, item.Taken)
                : new EditorDocument(original, null, item.Taken);
        }
    }

    /// <summary>The complete editable state, null only when edits are absent. Unsafe metadata
    /// is an error rather than permission to open the original.</summary>
    public static EditorState? LoadMarks(long id, BitmapSource? original = null)
    {
        var result = ReadMarks(id, original);
        if (result.Status == MarksStatus.Unreadable) throw new System.IO.InvalidDataException(Loc("Str_Marks_Unreadable"));
        return result.State;
    }

    public static MarksRead ReadMarks(long id, BitmapSource? original = null)
    {
        if (FolderCaptures.IsFolderId(id)) return new(MarksStatus.Absent, null);
        byte[]? baseImage;
        string marks;
        int version;
        lock (_gate)
        {
            if (_db == null) throw new InvalidOperationException(Loc("Str_Lib_LockedTitle"));
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT version, base_image, marks FROM capture_marks WHERE capture_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return new(MarksStatus.Absent, null);
            version = r.GetInt32(0);
            baseImage = r.IsDBNull(1) ? null : (byte[])r[1];
            marks = r.GetString(2);
        }
        if (version != MarksFormat.Version || MarksFormat.Read(marks) is not { } items) return new(MarksStatus.Unreadable, null);
        // Without a base picture the marks sit on the original itself; the caller's copy is reused so a
        // later save recognises it and stores no second copy.
        try
        {
            var image = baseImage != null ? Decode(baseImage, exactPixels: true) : original ?? LoadImage(id);
            return new(MarksStatus.Editable, new EditorState(image, items));
        }
        catch (Exception error) when (IsMarksReadError(error)) { return new(MarksStatus.Unreadable, null); }
    }

    private static bool IsMarksReadError(Exception error) => error is System.IO.IOException or FormatException or NotSupportedException
        or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException or OverflowException;

    /// <summary>What copying, printing, saving or dragging a capture gives: the flattened result
    /// of its marks, so a redaction made in the editor is never bypassed from the album.</summary>
    public static BitmapSource LoadOutput(CaptureItem item)
    {
        lock (_gate)
        {
            if (!IsCurrent(item)) throw new InvalidOperationException(Loc("Str_Album_Changed"));
            return LoadOutput(item.Id);
        }
    }

    public static BitmapSource LoadOutput(long id) =>
        EditedPng(id) is { } png ? Decode(png, exactPixels: true) : LoadImage(id);

    /// <summary>The flattened PNG for an edited capture, or the stored original.</summary>
    public static byte[] LoadOutputPng(long id) => EditedPng(id) ?? LoadPng(id);

    public static byte[] LoadOutputPng(CaptureItem item)
    {
        lock (_gate)
        {
            if (!IsCurrent(item)) throw new InvalidOperationException(Loc("Str_Album_Changed"));
            return LoadOutputPng(item.Id);
        }
    }

    /// <summary>The flattened result of a capture's marks, or null when it has none.</summary>
    private static byte[]? EditedPng(long id)
    {
        if (FolderCaptures.IsFolderId(id)) return null;
        lock (_gate)
        {
            if (_db == null) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT output_image FROM capture_marks WHERE capture_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteScalar() as byte[];
        }
    }
}
