using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Editor;
using KillerShot.Services;
using Microsoft.Data.Sqlite;

namespace KillerShot.Shell;

/// <summary>Run only inside a process memory limit. Uses compact, complete oversized image streams.</summary>
internal static class SelfTestAlbumImageAdmission
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length is not (2 or 3) || args[0] != "--selftest-album-image-admission") return false;
        string folder = Path.GetFullPath(args[1]);
        string format = args.Length == 3 ? args[2] : "png";
        var report = new List<string>();
        int exit = 1;
        try
        {
            Directory.CreateDirectory(folder);
            RequireMemoryLimit();
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException(CaptureLibrary.LoadError);
            byte[] oversized = Encoded(format, 10001, 10001);
            byte[] control = Encoded(format, 16, 16);
            File.WriteAllBytes(Path.Combine(folder, "complete-oversized." + format), oversized);
            File.WriteAllBytes(Path.Combine(folder, "control." + format), control);
            var small = ImageOutput.Load(Path.Combine(folder, "control." + format));
            if (small.PixelWidth != 16 || small.PixelHeight != 16) throw new InvalidOperationException("Independent image fixture failed.");
            var narrow = ImageOutput.Load(Encoded(format, 20000, 1));
            if (narrow.PixelWidth != 20000 || narrow.PixelHeight != 1) throw new InvalidOperationException("Valid extreme aspect ratio changed.");
            bool rejected = false;
            try { ImageOutput.Load(control[..Math.Min(12, control.Length)]); }
            catch (Exception error) when (error is IOException or FormatException or NotSupportedException or ArgumentException or InvalidOperationException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Truncated image data was accepted.");
            if (format == "png")
            {
                var large = ImageOutput.Load(Encoded(format, 4000, 3000));
                if (large.PixelWidth != 4000 || large.PixelHeight != 3000) throw new InvalidOperationException("Valid large image changed dimensions.");
            }
            report.Add(format + " valid control, extreme aspect ratio and malformed input PASS");
            long id = CaptureLibrary.AddNow(small, DateTime.Now, "region", "Admission fixture", null);
            var item = CaptureLibrary.Items().Single();
            byte[] original = CaptureLibrary.LoadPng(item);
            var doc = new EditorDocument(small);
            doc.Add(new ShapeAnnotation(ShapeKind.Redact, new Point(0, 0), new Point(16, 16), Colors.Black, 1));
            CaptureLibrary.SaveMarks(id, doc.State, small, doc.Render());
            string key = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(Path.Combine(folder, "library", "Library.key")),
                "KillerShot.Library.v1"u8.ToArray(), DataProtectionScope.CurrentUser));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CaptureLibrary.FilePath, Password = key, Pooling = false }.ConnectionString);
            db.Open();
            void Blob(string table, string column, byte[]? value)
            {
                using var update = db.CreateCommand();
                update.CommandText = "UPDATE " + table + " SET " + column + "=$data WHERE " + (table == "captures" ? "id" : "capture_id") + "=$id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$data", (object?)value ?? DBNull.Value);
                update.ExecuteNonQuery();
            }
            Blob("captures", "image", oversized);
            Reject(() => CaptureLibrary.LoadImage(item));
            Blob("captures", "image", original);
            report.Add(format + " oversized original rejected by admission PASS");
            Blob("captures", "thumb", oversized);
            Reject(() => CaptureLibrary.LoadThumb(item));
            report.Add(format + " oversized thumbnail rejected by admission PASS");
            byte[] flattened = CaptureLibrary.LoadOutputPng(item);
            Blob("capture_marks", "output_image", oversized);
            Reject(() => CaptureLibrary.LoadOutput(item));
            Blob("capture_marks", "output_image", flattened);
            report.Add(format + " oversized flattened output rejected by admission PASS");
            Blob("capture_marks", "base_image", oversized);
            if (CaptureLibrary.LoadDocument(item).Recovery != AlbumRecovery.Flattened)
                throw new InvalidOperationException("Oversized base did not use safe recovery.");
            Blob("capture_marks", "base_image", null);
            var marks = System.Text.Json.Nodes.JsonNode.Parse(MarksFormat.Write([new ImageAnnotation(small, new Rect(0, 0, 16, 16))]))!;
            marks["marks"]![0]!["png"] = Convert.ToBase64String(oversized);
            using (var update = db.CreateCommand())
            {
                update.CommandText = "UPDATE capture_marks SET marks=$marks WHERE capture_id=$id";
                update.Parameters.AddWithValue("$id", id);
                update.Parameters.AddWithValue("$marks", marks.ToJsonString());
                update.ExecuteNonQuery();
            }
            if (CaptureLibrary.LoadDocument(item).Recovery != AlbumRecovery.Flattened)
                throw new InvalidOperationException("Oversized inserted mark did not use safe recovery.");
            report.Add(format + " oversized base and inserted mark safely recovered PASS");
            exit = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            CaptureLibrary.Close();
            File.WriteAllLines(Path.Combine(folder, "album-image-admission-report.txt"), report);
            App.Instance.Shutdown(exit);
        }
        return true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int informationClass, byte[] information, uint length, IntPtr returnedLength);

    private static void RequireMemoryLimit()
    {
        // This executable targets x64. Extended job information has flags at 16 and the
        // process memory limit at 112. Refuse uncontained invocations before creating fixtures.
        var information = new byte[144];
        if (!QueryInformationJobObject(IntPtr.Zero, 9, information, (uint)information.Length, IntPtr.Zero)
            || (BitConverter.ToUInt32(information, 16) & 0x2100) != 0x2100
            || BitConverter.ToUInt64(information, 112) is 0 or > 536870912)
            throw new InvalidOperationException("Run this case inside a kill-on-close job with a process memory cap of at most 512 MiB.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ImageTooLargeException) { return; }
        throw new InvalidOperationException("An oversized album image bypassed header admission.");
    }

    private static byte[] Encoded(string format, int width, int height) => format switch
    {
        "png" => Png(width, height), "jpg" => Jpeg(width, height), "tif" => Tiff(width, height),
        _ => throw new ArgumentException("Unsupported fixture format.")
    };

    private static byte[] Png(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 1; // One-bit grayscale keeps the complete encoded fixture small.
        Chunk(stream, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + (width + 7) / 8];
            for (int y = 0; y < height; y++) zip.Write(row);
        }
        Chunk(stream, "IDAT", compressed.ToArray());
        Chunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void Chunk(Stream stream, string kind, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);
        byte[] type = Encoding.ASCII.GetBytes(kind);
        stream.Write(type); stream.Write(data);
        uint crc = 0xffffffff;
        foreach (byte value in type.Concat(data))
        {
            crc ^= value;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        stream.Write(number);
    }

    private static byte[] Tiff(int width, int height)
    {
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[(width + 7) / 8];
            for (int y = 0; y < height; y++) zip.Write(row);
        }
        byte[] pixels = compressed.ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4949); writer.Write((ushort)42); writer.Write(8);
        const int entries = 9, offset = 8 + 2 + entries * 12 + 4;
        int count = pixels.Length;
        writer.Write((ushort)entries);
        void Entry(ushort tag, ushort type, uint value) { writer.Write(tag); writer.Write(type); writer.Write(1u); writer.Write(value); }
        Entry(256, 4, (uint)width); Entry(257, 4, (uint)height); Entry(258, 3, 1);
        Entry(259, 3, 8); Entry(262, 3, 1); Entry(273, 4, offset);
        Entry(277, 3, 1); Entry(278, 4, (uint)height); Entry(279, 4, (uint)count);
        writer.Write(0u);
        stream.Write(pixels);
        return stream.ToArray();
    }

    private static byte[] Jpeg(int width, int height)
    {
        using var stream = new MemoryStream();
        stream.Write([255, 216]);
        void Segment(byte marker, byte[] payload)
        {
            stream.WriteByte(255); stream.WriteByte(marker);
            stream.WriteByte((byte)((payload.Length + 2) >> 8)); stream.WriteByte((byte)(payload.Length + 2));
            stream.Write(payload);
        }
        Segment(219, new byte[] { 0 }.Concat(Enumerable.Repeat((byte)1, 64)).ToArray());
        Segment(192, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]);
        var table = new byte[18]; table[1] = 1;
        Segment(196, table);
        table = (byte[])table.Clone(); table[0] = 0x10;
        Segment(196, table);
        Segment(218, [1, 1, 0, 0, 63, 0]);
        int bits = checked(((width + 7) / 8) * ((height + 7) / 8) * 2);
        stream.Write(new byte[bits / 8]);
        if (bits % 8 != 0) stream.WriteByte((byte)((1 << (8 - bits % 8)) - 1));
        stream.Write([255, 217]);
        return stream.ToArray();
    }
}
