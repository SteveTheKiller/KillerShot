using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestOutputFiles
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length < 2 || args[0] != "--selftest-output-files") return false;
        string folder = Path.Combine(Path.GetFullPath(args[1]), $"run-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        int exit = 1;
        string report;
        try
        {
            string first = OutputFiles.NextPath(folder, "capture.mp4");
            string second = OutputFiles.NextPath(folder, "capture.mp4");
            if (first == second)
                throw new InvalidOperationException("Two simultaneous outputs received the same path.");
            if (!File.Exists(first) || !File.Exists(second))
                throw new InvalidOperationException("The recording paths were not reserved on disk.");

            string existing = Path.Combine(folder, "existing.png");
            File.WriteAllText(existing, "original");
            try
            {
                OutputFiles.WriteAtomic(existing, true, stream =>
                {
                    stream.WriteByte(1);
                    throw new IOException("Simulated interrupted write.");
                });
                throw new InvalidOperationException("The interrupted write unexpectedly succeeded.");
            }
            catch (IOException ex) when (ex.Message == "Simulated interrupted write.") { }
            if (File.ReadAllText(existing) != "original")
                throw new InvalidOperationException("An interrupted write changed the existing image.");
            try
            {
                OutputFiles.WriteAtomic(existing, false, stream => stream.WriteByte(2));
                throw new InvalidOperationException("A new-only write replaced an existing image.");
            }
            catch (IOException) when (File.ReadAllText(existing) == "original") { }
            OutputFiles.WriteAtomic(existing, true, stream => stream.WriteByte(3));
            if (File.ReadAllBytes(existing) is not [3])
                throw new InvalidOperationException("An explicit replacement did not publish its new content.");

            var paths = new ConcurrentBag<string>();
            Parallel.For(0, 24, i => paths.Add(OutputFiles.WriteUnique(folder, "parallel.png", stream => stream.WriteByte((byte)i))));
            if (paths.Count != 24 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 24)
                throw new InvalidOperationException("Concurrent image writes reused a path.");
            if (paths.Select(path => File.ReadAllBytes(path)[0]).Distinct().Count() != 24)
                throw new InvalidOperationException("Concurrent image writes lost content.");

            string priorVideo = Path.Combine(folder, "video.mp4");
            File.WriteAllText(priorVideo, "prior");
            string stageFolder = Path.Combine(folder, "recording-stage");
            string stage = OutputFiles.NextPath(stageFolder, "recording.mp4");
            File.WriteAllText(stage, "complete");
            string published = OutputFiles.PublishUnique(stage, folder, "video.mp4");
            if (published == priorVideo || File.ReadAllText(priorVideo) != "prior"
                || File.ReadAllText(published) != "complete" || File.Exists(stage))
                throw new InvalidOperationException("A recording publication replaced an earlier video or left its staging file.");
            Directory.Delete(stageFolder);

            var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
            pixel.Freeze();
            foreach (string extension in new[] { ".gif", ".tif", ".tiff", ".bmp", ".webp" })
            {
                string unsupported = Path.Combine(folder, "preserve" + extension);
                byte[] original = [1, 2, 3, 4];
                File.WriteAllBytes(unsupported, original);
                bool rejected = false;
                try { ImageOutput.Save(pixel, unsupported); }
                catch (NotSupportedException) { rejected = true; }
                if (!rejected || !File.ReadAllBytes(unsupported).SequenceEqual(original))
                    throw new InvalidOperationException("Unsupported image save changed the original: " + extension);
            }
            string image1 = ImageOutput.SaveUnique(pixel, folder, "image.png");
            string image2 = ImageOutput.SaveUnique(pixel, folder, "image.png");
            if (image1 == image2 || !File.Exists(image1) || !File.Exists(image2))
                throw new InvalidOperationException("Image saves did not use unique names.");
            if (!ImageOutput.CanSaveInPlace(image1))
                throw new InvalidOperationException("A single-frame PNG cannot be saved in place.");
            string jpeg = Path.Combine(folder, "image.jpg");
            ImageOutput.Save(pixel, jpeg);
            if (!ImageOutput.CanSaveInPlace(jpeg))
                throw new InvalidOperationException("A single-frame JPEG cannot be saved in place.");
            string mismatch = Path.Combine(folder, "mismatch.png");
            File.Copy(jpeg, mismatch);
            if (ImageOutput.CanSaveInPlace(mismatch))
                throw new InvalidOperationException("A JPEG with a PNG extension allows in-place saving.");
            string multiPage = Path.Combine(folder, "two-pages.tif");
            var tiff = new TiffBitmapEncoder();
            tiff.Frames.Add(BitmapFrame.Create(pixel));
            tiff.Frames.Add(BitmapFrame.Create(pixel));
            using (var stream = File.Create(multiPage)) tiff.Save(stream);
            byte[] originalTiff = File.ReadAllBytes(multiPage);
            if (ImageOutput.CanSaveInPlace(multiPage))
                throw new InvalidOperationException("A two-page TIFF allows in-place saving.");
            string disguisedTiff = Path.Combine(folder, "two-pages.png");
            File.Copy(multiPage, disguisedTiff);
            if (ImageOutput.CanSaveInPlace(disguisedTiff))
                throw new InvalidOperationException("A two-page TIFF with a PNG extension allows in-place saving.");
            ImageOutput.Save(ImageOutput.Load(multiPage), Path.Combine(folder, "export.png"));
            if (!File.ReadAllBytes(multiPage).SequenceEqual(originalTiff))
                throw new InvalidOperationException("Exporting a TIFF changed its source.");
            if (Directory.EnumerateFiles(folder, ".KillerShot-*.tmp").Any())
                throw new InvalidOperationException("A completed or failed write left a staging file.");
            report = "Output reservations, atomic replacement, concurrent unique writes, video publication, and image saves: PASS";
            exit = 0;
        }
        catch (Exception ex) { report = ex.ToString(); }
        File.WriteAllText(Path.Combine(folder, "output-files-report.txt"), report, Encoding.UTF8);
        App.Current.Shutdown(exit);
        return true;
    }
}
