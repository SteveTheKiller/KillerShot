using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Capture;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Headless check: KillerShot.exe --selftest-scroll-stitch &lt;folder&gt;. Synthetic pages are
/// scrolled by uneven steps under a sticky header and footer, with repeated frames, pauses and a
/// blinking area; the stitched result must equal the page exactly, with no missing or repeated
/// rows. Sideways scrolling, a size limit and a jump too far to match are checked too.</summary>
internal static class SelfTestScrollStitch
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-scroll-stitch") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        var report = new List<string>();
        int code = 1;
        try
        {
            Vertical(report, folder);
            Sideways(report);
            Limits(report);
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        File.WriteAllLines(Path.Combine(folder, "scroll-stitch-report.txt"), report);
        App.Instance.Shutdown(code);
        return true;
    }

    private static void Expect(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    /// <summary>A page whose every row is different, so a misplaced row cannot go unnoticed.</summary>
    private static byte[] Page(int w, int h, int seed)
    {
        var rng = new Random(seed);
        var px = new byte[w * h * 4];
        rng.NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        return px;
    }

    private static byte[] Rows(byte[] page, int w, int from, int count)
    {
        var part = new byte[w * count * 4];
        Buffer.BlockCopy(page, from * w * 4, part, 0, part.Length);
        return part;
    }

    private static byte[] Pixels(BitmapSource bmp)
    {
        var px = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(px, bmp.PixelWidth * 4, 0);
        return px;
    }

    private static void Vertical(List<string> report, string folder)
    {
        const int W = 200, H = 300, Header = 40, Footer = 25, Band = H - Header - Footer, PageRows = 2400;
        var header = Page(W, Header, 1);
        var footer = Page(W, Footer, 2);
        var page = Page(W, PageRows, 3);
        byte[] Frame(int scroll, bool blink = false)
        {
            var f = new byte[W * H * 4];
            Buffer.BlockCopy(header, 0, f, 0, header.Length);
            Buffer.BlockCopy(page, scroll * W * 4, f, Header * W * 4, Band * W * 4);
            Buffer.BlockCopy(footer, 0, f, (H - Footer) * W * 4, footer.Length);
            if (blink) for (int y = Header + 20; y < Header + 26; y++) for (int x = 10; x < 40; x++) f[(y * W + x) * 4] ^= 0xFF;
            return f;
        }
        var stitcher = new ScrollStitcher(W, H, 100_000_000);
        var steps = new[] { 0, 0, 7, 7, 40, 222, 1, 1, 90, 150, 220, 3, 200, 180, 225, 160 };   // repeats are pauses; 225 is a page down
        int scroll = 0;
        foreach (int step in steps)
        {
            scroll = Math.Min(PageRows - Band, scroll + step);
            stitcher.Add(Frame(scroll, blink: step == 90));
        }
        while (scroll < PageRows - Band) { scroll = Math.Min(PageRows - Band, scroll + 211); stitcher.Add(Frame(scroll)); }
        var result = stitcher.Result();
        ImageOutput.SavePng(result, Path.Combine(folder, "stitched-vertical.png"));
        var expected = new byte[W * (Header + PageRows + Footer) * 4];
        Buffer.BlockCopy(header, 0, expected, 0, header.Length);
        Buffer.BlockCopy(page, 0, expected, Header * W * 4, page.Length);
        Buffer.BlockCopy(footer, 0, expected, (Header + PageRows) * W * 4, footer.Length);
        Expect(result.PixelWidth == W && result.PixelHeight == Header + PageRows + Footer,
            $"The stitched page is {result.PixelWidth} x {result.PixelHeight}, expected {W} x {Header + PageRows + Footer}.");
        Expect(Pixels(result).AsSpan().SequenceEqual(expected) && !stitcher.LostTrack,
            "The stitched page has missing, repeated or altered rows.");
        report.Add($"vertical: uneven steps, pauses, blink, sticky header and footer, exact {W} x {result.PixelHeight} PASS");
    }

    private static void Sideways(List<string> report)
    {
        const int W = 240, H = 120, PageColumns = 1500;
        var page = Page(PageColumns, H, 4);   // a wide page, row-major
        byte[] Frame(int scroll)
        {
            var f = new byte[W * H * 4];
            for (int y = 0; y < H; y++) Buffer.BlockCopy(page, (y * PageColumns + scroll) * 4, f, y * W * 4, W * 4);
            return f;
        }
        var stitcher = new ScrollStitcher(W, H, 100_000_000);
        int scroll = 0;
        stitcher.Add(Frame(0));
        foreach (int step in new[] { 13, 170, 60, 160, 5, 140 }) stitcher.Add(Frame(scroll += step));
        while (scroll < PageColumns - W) stitcher.Add(Frame(scroll = Math.Min(PageColumns - W, scroll + 150)));
        var result = stitcher.Result();
        Expect(result.PixelWidth == PageColumns && result.PixelHeight == H && Pixels(result).AsSpan().SequenceEqual(page),
            $"Sideways stitching is {result.PixelWidth} x {result.PixelHeight} or wrong.");
        report.Add($"sideways: exact {result.PixelWidth} x {H} PASS");
    }

    private static void Limits(List<string> report)
    {
        const int W = 100, H = 100, PageRows = 5000;
        var page = Page(W, PageRows, 5);
        var limited = new ScrollStitcher(W, H, W * 600L);
        for (int s = 0; s <= PageRows - H; s += 60) limited.Add(Rows(page, W, s, H));
        var capped = limited.Result();
        Expect(limited.Full && capped.PixelHeight <= 600 && capped.PixelHeight >= 590, "The size limit did not stop the picture: " + capped.PixelHeight);
        Expect(Pixels(capped).AsSpan().SequenceEqual(Rows(page, W, 0, capped.PixelHeight)), "The capped picture is not the top of the page.");

        var lost = new ScrollStitcher(W, H, 100_000_000);
        lost.Add(Rows(page, W, 0, H));
        lost.Add(Rows(page, W, 40, H));
        lost.Add(Rows(page, W, 900, H));   // scrolled far past the window: no overlap to match
        Expect(lost.LostTrack && lost.Result().PixelHeight == 140, "A jump with no overlap was not reported, or it changed the picture.");
        report.Add("size limit stops cleanly; a jump with no overlap is reported and keeps what matched PASS");
    }
}
