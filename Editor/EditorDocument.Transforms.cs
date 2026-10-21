using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Editor;

/// <summary>Turning, mirroring, resizing, a margin around the picture and trimming a plain border.
/// Each is one undo step, and the marks move with the picture.</summary>
public sealed partial class EditorDocument
{
    /// <summary>A quarter turn: clockwise when <paramref name="clockwise"/>, else counterclockwise.</summary>
    public bool Rotate(bool clockwise)
    {
        var (px, w, h) = Pixels(State.Image);
        var turned = new byte[px.Length];
        // The turned picture is h wide and w tall.
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = clockwise ? h - 1 - y : y, dy = clockwise ? x : w - 1 - x;
                Buffer.BlockCopy(px, (y * w + x) * 4, turned, (dy * h + dx) * 4, 4);
            }
        var m = clockwise ? new Matrix(0, 1, -1, 0, h, 0) : new Matrix(0, -1, 1, 0, 0, w);
        return Replace(Bitmap(turned, h, w), m, 1);
    }

    /// <summary>Mirrors left to right, or top to bottom.</summary>
    public bool Flip(bool horizontal)
    {
        var (px, w, h) = Pixels(State.Image);
        var flipped = new byte[px.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int dx = horizontal ? w - 1 - x : x, dy = horizontal ? y : h - 1 - y;
                Buffer.BlockCopy(px, (y * w + x) * 4, flipped, (dy * w + dx) * 4, 4);
            }
        var m = horizontal ? new Matrix(-1, 0, 0, 1, w, 0) : new Matrix(1, 0, 0, -1, 0, h);
        return Replace(Bitmap(flipped, w, h), m, 1);
    }

    /// <summary>Resamples the picture to a new pixel size. The marks scale with it.</summary>
    public bool Scale(int width, int height)
    {
        int w = PixelWidth, h = PixelHeight;
        if (width < 1 || height < 1 || (width == w && height == h)) return false;
        if ((long)width * height > Services.ImageOutput.MaxPixels) throw new Services.ImageTooLargeException(width, height);
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(State.Image, new Rect(0, 0, width, height));
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var (px, _, _) = Pixels(new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0));
        double sx = (double)width / w, sy = (double)height / h;
        return Replace(Bitmap(px, width, height), new Matrix(sx, 0, 0, sy, 0, 0), Math.Sqrt(sx * sy));
    }

    /// <summary>Adds a border of <paramref name="size"/> pixels in a color around the picture.</summary>
    public bool AddMargin(int size, Color color)
    {
        if (size < 1) return false;
        var (px, w, h) = Pixels(State.Image);
        int nw = w + size * 2, nh = h + size * 2;
        if ((long)nw * nh > Services.ImageOutput.MaxPixels) throw new Services.ImageTooLargeException(nw, nh);
        var framed = new byte[nw * nh * 4];
        for (int i = 0; i < framed.Length; i += 4) { framed[i] = color.B; framed[i + 1] = color.G; framed[i + 2] = color.R; framed[i + 3] = color.A; }
        for (int y = 0; y < h; y++) Buffer.BlockCopy(px, y * w * 4, framed, ((y + size) * nw + size) * 4, w * 4);
        return Replace(Bitmap(framed, nw, nh), new Matrix(1, 0, 0, 1, size, size), 1);
    }

    /// <summary>The picture without a plain border: the rows and columns at the edges that match
    /// the top-left pixel within <paramref name="tolerance"/>. False when there is no such border.</summary>
    public bool AutoCrop(int tolerance = 12)
    {
        var (px, w, h) = Pixels(State.Image);
        byte b0 = px[0], g0 = px[1], r0 = px[2], a0 = px[3];
        bool Plain(int x, int y)
        {
            int i = (y * w + x) * 4;
            return Math.Abs(px[i] - b0) <= tolerance && Math.Abs(px[i + 1] - g0) <= tolerance
                && Math.Abs(px[i + 2] - r0) <= tolerance && Math.Abs(px[i + 3] - a0) <= tolerance;
        }
        bool RowPlain(int y) { for (int x = 0; x < w; x++) if (!Plain(x, y)) return false; return true; }
        bool ColumnPlain(int x, int top, int bottom) { for (int y = top; y <= bottom; y++) if (!Plain(x, y)) return false; return true; }
        int t = 0, b = h - 1;
        while (t <= b && RowPlain(t)) t++;
        if (t > b) return false;   // nothing but border
        while (RowPlain(b)) b--;
        int l = 0, r = w - 1;
        while (ColumnPlain(l, t, b)) l++;
        while (ColumnPlain(r, t, b)) r--;
        return Crop(new Int32Rect(l, t, r - l + 1, b - t + 1));
    }

    private bool Replace(BitmapSource image, Matrix m, double scale)
    {
        Apply(new EditorState(image, State.Items.Select(a => a.Transform(m, scale)).ToImmutableList()));
        return true;
    }

    private static (byte[] Pixels, int Width, int Height) Pixels(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var px = new byte[(long)w * h * 4];
        bgra.CopyPixels(px, w * 4, 0);
        return (px, w, h);
    }

    private static BitmapSource Bitmap(byte[] px, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }
}
