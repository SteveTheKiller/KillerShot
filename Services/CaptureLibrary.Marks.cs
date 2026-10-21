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

    private static void RaiseCaptureStored(BitmapSource bmp, long id)
    {
        try { CaptureStored?.Invoke(bmp, id); }
        catch (Exception error) { System.Diagnostics.Trace.TraceWarning("Capture link failed: {0}", error.Message); }
    }

    /// <summary>Stores the editor's current marks and base picture for a capture, or forgets them
    /// when the picture is back to the plain original, and shows the flattened result as its
    /// thumbnail. The caller renders <paramref name="flattened"/> on the interface thread.</summary>
    public static void SaveMarks(long id, EditorState state, BitmapSource original, BitmapSource flattened)
    {
        bool plain = state.Items.IsEmpty && ReferenceEquals(state.Image, original);
        byte[]? baseImage = ReferenceEquals(state.Image, original) ? null : ImageOutput.EncodePng(state.Image);
        string marks = MarksFormat.Write(state.Items);
        byte[] thumb = EncodeThumb(plain ? original : flattened);
        byte[]? output = plain ? null : ImageOutput.EncodePng(flattened);
        lock (_gate)
        {
            if (_db == null) return;
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
                    """;
                cmd.Parameters.AddWithValue("$thumb", thumb);
                cmd.Parameters.AddWithValue("$id", id);
                if (cmd.ExecuteNonQuery() == 0) return;   // deleted meanwhile; roll back
            }
            IndexText(id, transaction);
            transaction.Commit();
            _marksRevision[id] = MarksRevision(id) + 1;
            lock (_ocrFailed) _ocrFailed.Remove(id);
            ClearThumbCache();
        }
        RaiseChanged();
        if (BackgroundWork)
        {
            StartOcr();
            StartQrReading();
        }
    }

    /// <summary>Bumped under the gate whenever a capture's marks change, so a background reader that
    /// read the picture before the edit drops its result instead of storing text the edit hid.</summary>
    private static readonly Dictionary<long, int> _marksRevision = [];

    private static int MarksRevision(long id) => _marksRevision.TryGetValue(id, out int revision) ? revision : 0;

    /// <summary>The capture as the editor left it: its base picture and marks, or null when it
    /// has none (or they were written by a newer version, in which case the original opens).</summary>
    public static EditorState? LoadMarks(long id, BitmapSource? original = null)
    {
        if (FolderCaptures.IsFolderId(id)) return null;
        byte[]? baseImage;
        string marks;
        int version;
        lock (_gate)
        {
            if (_db == null) return null;
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT version, base_image, marks FROM capture_marks WHERE capture_id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            version = r.GetInt32(0);
            baseImage = r.IsDBNull(1) ? null : (byte[])r[1];
            marks = r.GetString(2);
        }
        if (version != MarksFormat.Version || MarksFormat.Read(marks) is not { } items) return null;
        // Without a base picture the marks sit on the original itself; the caller's copy is reused so a
        // later save recognises it and stores no second copy.
        var image = baseImage != null ? Decode(baseImage, exactPixels: true) : original ?? LoadImage(id);
        return new EditorState(image, items);
    }

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
