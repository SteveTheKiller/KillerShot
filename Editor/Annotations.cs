using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Editor;

public enum ToolKind { Select, Pen, Highlighter, Arrow, Line, Rectangle, Ellipse, Text, Step, Callout, Redact, Crop }

/// <summary>One mark on the capture, in image pixel coordinates. Annotations are immutable so an
/// undo step is just the previous list.</summary>
public abstract record Annotation(Color Color, double Width)
{
    public abstract void Render(DrawingContext dc);
    public abstract Rect Bounds { get; }
    public abstract Annotation Offset(Vector v);
    /// <summary>The same mark with its bounds grown or shrunk by d, keeping its top left corner.
    /// Marks stay at least two pixels in each direction.</summary>
    public abstract Annotation Resize(Vector d);
    /// <summary>The same mark after the picture is rotated, flipped or scaled by m. Widths and
    /// font sizes scale by <paramref name="scale"/>; text stays upright, centered where it was.</summary>
    public abstract Annotation Transform(Matrix m, double scale);

    /// <summary>Moves an upright mark so its center lands where m takes it.</summary>
    protected Vector CenterShift(Matrix m, Rect bounds, Size scaledSize)
    {
        var c = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        var to = m.Transform(c);
        return new Vector(to.X - scaledSize.Width / 2, to.Y - scaledSize.Height / 2);
    }
    public virtual bool Hit(Point p, double tolerance) => Inflate(Bounds, tolerance).Contains(p);

    protected static Rect Inflate(Rect r, double by)
    {
        if (r.IsEmpty) return r;
        r.Inflate(by, by);
        return r;
    }

    protected static Brush Frozen(Brush b) { if (b.CanFreeze) b.Freeze(); return b; }

    protected static Pen MakePen(Color c, double w, PenLineCap cap = PenLineCap.Round)
    {
        var pen = new Pen(new SolidColorBrush(c), w) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}

/// <summary>Freehand pen or highlighter stroke.</summary>
public sealed record StrokeAnnotation(ImmutableArray<Point> Points, Color Color, double Width, bool Highlighter)
    : Annotation(Color, Width)
{
    private Geometry? _geometry;

    public Geometry Geometry => _geometry ??= Build();

    private Geometry Build()
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            if (Points.Length > 0)
            {
                ctx.BeginFigure(Points[0], false, false);
                if (Points.Length == 1) ctx.LineTo(Points[0] + new Vector(0.01, 0), true, true);
                for (int i = 1; i < Points.Length; i++) ctx.LineTo(Points[i], true, true);
            }
        }
        g.Freeze();
        return g;
    }

    private Pen StrokePen => Highlighter
        ? MakePen(Color.FromArgb(0x70, Color.R, Color.G, Color.B), Width, PenLineCap.Square)
        : MakePen(Color, Width);

    public override void Render(DrawingContext dc) => dc.DrawGeometry(null, StrokePen, Geometry);

    public override Rect Bounds => Geometry.GetRenderBounds(StrokePen);

    public override Annotation Offset(Vector v) =>
        this with { Points = Points.Select(p => p + v).ToImmutableArray(), _geometry = null };

    public override Annotation Transform(Matrix m, double scale) =>
        this with { Points = Points.Select(m.Transform).ToImmutableArray(), Width = Width * scale, _geometry = null };

    public override Annotation Resize(Vector d)
    {
        var b = Geometry.Bounds;
        double sx = b.Width > 0 ? Math.Max(2, b.Width + d.X) / b.Width : 1;
        double sy = b.Height > 0 ? Math.Max(2, b.Height + d.Y) / b.Height : 1;
        return this with { Points = Points.Select(p => new Point(b.X + (p.X - b.X) * sx, b.Y + (p.Y - b.Y) * sy)).ToImmutableArray(), _geometry = null };
    }

    public override bool Hit(Point p, double tolerance) =>
        Geometry.StrokeContains(MakePen(Colors.Black, Width + tolerance * 2), p);
}

public enum ShapeKind { Arrow, Rectangle, Ellipse, Redact, Line }

/// <summary>Arrow, line, rectangle, ellipse or redaction box between two points. Rectangles and
/// ellipses can be filled with their color.</summary>
public sealed record ShapeAnnotation(ShapeKind Kind, Point A, Point B, Color Color, double Width, bool Filled = false)
    : Annotation(Color, Width)
{
    public Rect Box => new(A, B);

    /// <summary>The box expanded outward to the image pixel grid.</summary>
    public Rect PixelCells
    {
        get
        {
            var b = Box;
            double l = Math.Floor(b.Left), t = Math.Floor(b.Top);
            return new Rect(l, t, Math.Ceiling(b.Right) - l, Math.Ceiling(b.Bottom) - t);
        }
    }

    public override void Render(DrawingContext dc)
    {
        switch (Kind)
        {
            case ShapeKind.Rectangle:
                dc.DrawRectangle(Filled ? Frozen(new SolidColorBrush(Color)) : null, MakePen(Color, Width, PenLineCap.Square), Box);
                break;
            case ShapeKind.Ellipse:
                var b = Box;
                dc.DrawEllipse(Filled ? Frozen(new SolidColorBrush(Color)) : null, MakePen(Color, Width), new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                break;
            case ShapeKind.Line:
                dc.DrawLine(MakePen(Color, Width), A, B);
                break;
            case ShapeKind.Redact:
                // Fully opaque and grown out to whole pixels, so every pixel the box touches is
                // replaced, never blended at a fractional edge.
                dc.DrawRectangle(Frozen(new SolidColorBrush(Colors.Black)), null, PixelCells);
                break;
            default:
                dc.DrawGeometry(Frozen(new SolidColorBrush(Color)), MakePen(Color, Width), ArrowGeometry());
                break;
        }
    }

    private Geometry ArrowGeometry()
    {
        var g = new StreamGeometry();
        Vector d = B - A;
        double len = d.Length;
        using (var ctx = g.Open())
        {
            if (len < 0.5)
            {
                ctx.BeginFigure(A, false, false);
                ctx.LineTo(A + new Vector(0.01, 0), true, true);
            }
            else
            {
                d /= len;
                double head = Math.Min(len, Math.Max(10, Width * 4));
                var n = new Vector(-d.Y, d.X);
                Point baseCenter = B - d * head;
                ctx.BeginFigure(A, false, false);
                ctx.LineTo(baseCenter, true, true);
                ctx.BeginFigure(B, true, true);
                ctx.LineTo(baseCenter + n * head * 0.45, true, true);
                ctx.LineTo(baseCenter - n * head * 0.45, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    public override Rect Bounds
    {
        get
        {
            var r = Kind == ShapeKind.Redact ? PixelCells : Box;
            if (Kind != ShapeKind.Redact) r = Inflate(r, Kind == ShapeKind.Arrow ? Math.Max(10, Width * 4) : Width / 2);
            return r;
        }
    }

    public override Annotation Offset(Vector v) => this with { A = A + v, B = B + v };

    public override Annotation Transform(Matrix m, double scale)
    {
        Point a = m.Transform(A), b = m.Transform(B);
        if (Kind is ShapeKind.Arrow or ShapeKind.Line) return this with { A = a, B = b, Width = Width * scale };
        // Boxes stay axis-aligned: normalize the corners after a turn or a mirror.
        var box = new Rect(a, b);
        return this with { A = box.TopLeft, B = box.BottomRight, Width = Width * scale };
    }

    public override Annotation Resize(Vector d)
    {
        if (Kind is ShapeKind.Arrow or ShapeKind.Line) return this with { B = B + d };
        var r = Box;
        return this with { A = r.TopLeft, B = new Point(r.X + Math.Max(2, r.Width + d.X), r.Y + Math.Max(2, r.Height + d.Y)) };
    }

    public override bool Hit(Point p, double tolerance)
    {
        if (Kind == ShapeKind.Redact) return Inflate(PixelCells, tolerance).Contains(p);   // as drawn
        if (Kind == ShapeKind.Line)
            return new LineGeometry(A, B).StrokeContains(MakePen(Colors.Black, Width + tolerance * 2), p);
        if (Kind == ShapeKind.Arrow)
            return ArrowGeometry().StrokeContains(MakePen(Colors.Black, Width + tolerance * 2), p) || ArrowGeometry().FillContains(p);
        if (Filled)
        {
            var area = Inflate(Box, Width / 2 + tolerance);
            return Kind == ShapeKind.Rectangle ? area.Contains(p) : InEllipse(area, p);
        }
        // Outlines are picked by their edge so a click inside a box can reach what is under it.
        var outer = Inflate(Box, Width / 2 + tolerance);
        var inner = Inflate(Box, -(Width / 2 + tolerance));
        if (Kind == ShapeKind.Rectangle) return outer.Contains(p) && (inner.IsEmpty || !inner.Contains(p));
        return InEllipse(outer, p) && (inner.IsEmpty || inner.Width <= 0 || inner.Height <= 0 || !InEllipse(inner, p));
    }

    private static bool InEllipse(Rect r, Point p)
    {
        if (r.Width <= 0 || r.Height <= 0) return false;
        double rx = r.Width / 2, ry = r.Height / 2;
        double x = (p.X - r.X - rx) / rx, y = (p.Y - r.Y - ry) / ry;
        return x * x + y * y <= 1;
    }
}

/// <summary>A line or block of text. Width is the font size in image pixels.</summary>
public sealed record TextAnnotation(Point Origin, string Text, Color Color, double Width) : Annotation(Color, Width)
{
    public static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public FormattedText Format() => new(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, Width,
        new SolidColorBrush(Color), 1.0) { TextAlignment = TextAlignment.Left };

    public override void Render(DrawingContext dc)
    {
        // A soft dark halo keeps light text readable on light screenshots and dark on dark.
        var ft = Format();
        var outline = ft.BuildGeometry(Origin);
        var halo = Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114 > 140 ? Color.FromArgb(0xB0, 0, 0, 0) : Color.FromArgb(0xB0, 255, 255, 255);
        dc.DrawGeometry(null, MakePen(halo, Math.Max(2, Width / 8)), outline);
        dc.DrawText(ft, Origin);
    }

    public override Rect Bounds
    {
        get
        {
            var ft = Format();
            return new Rect(Origin, new Size(Math.Max(1, ft.WidthIncludingTrailingWhitespace), ft.Height));
        }
    }

    public override Annotation Offset(Vector v) => this with { Origin = Origin + v };

    public override Annotation Transform(Matrix m, double scale)
    {
        var scaled = this with { Width = Width * scale };
        var size = scaled.Bounds.Size;
        return scaled with { Origin = (Point)CenterShift(m, Bounds, size) };
    }

    /// <summary>Text grows by its font size; the vertical change sets the new line height.</summary>
    public override Annotation Resize(Vector d)
    {
        double height = Bounds.Height;
        return height <= 0 ? this : this with { Width = Math.Max(6, Width * Math.Max(2, height + d.Y) / height) };
    }
}

/// <summary>A numbered step badge: a filled circle of the mark color with its number. Width is the
/// badge diameter in image pixels.</summary>
public sealed record StepAnnotation(Point Center, int Number, Color Color, double Width) : Annotation(Color, Width)
{
    public static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    public override void Render(DrawingContext dc)
    {
        double r = Width / 2;
        dc.DrawEllipse(Frozen(new SolidColorBrush(Color)), MakePen(Color.FromArgb(0xC0, 255, 255, 255), Math.Max(1, Width / 16)), Center, r, r);
        bool light = Color.R * 0.299 + Color.G * 0.587 + Color.B * 0.114 > 150;
        var ft = new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Face, Width * (Number < 10 ? 0.58 : 0.46), new SolidColorBrush(light ? Colors.Black : Colors.White), 1.0);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }

    public override Rect Bounds => new(Center.X - Width / 2, Center.Y - Width / 2, Width, Width);
    public override Annotation Offset(Vector v) => this with { Center = Center + v };

    public override Annotation Resize(Vector d)
    {
        // Either arrow resizes a badge, so both directions shrink as well as grow.
        double size = Math.Max(12, Width + (d.X != 0 ? d.X : d.Y));
        var b = Bounds;
        return this with { Width = size, Center = new Point(b.X + size / 2, b.Y + size / 2) };
    }

    public override bool Hit(Point p, double tolerance) => (p - Center).Length <= Width / 2 + tolerance;

    public override Annotation Transform(Matrix m, double scale) => this with { Center = m.Transform(Center), Width = Width * scale };

    /// <summary>The number a new step takes: one past the highest on the picture, so deleting a
    /// step never makes the next one repeat a number still shown.</summary>
    public static int Next(IEnumerable<Annotation> items) =>
        items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;
}

/// <summary>A speech-bubble callout: text in a white rounded box at Origin, with a tail pointing
/// at Tip. Width is the font size in image pixels.</summary>
public sealed record CalloutAnnotation(Point Tip, Point Origin, string Text, Color Color, double Width) : Annotation(Color, Width)
{
    private FormattedText Format() => new(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, TextAnnotation.Face, Width,
        new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)), 1.0);

    private double Pad => Math.Max(6, Width * 0.45);

    /// <summary>The bubble's rectangle, sized to its text.</summary>
    public Rect BubbleRect
    {
        get
        {
            var ft = Format();
            return new Rect(Origin, new Size(Math.Max(1, ft.WidthIncludingTrailingWhitespace) + Pad * 2, ft.Height + Pad * 2));
        }
    }

    private Geometry Shape()
    {
        var box = BubbleRect;
        double radius = Math.Min(Pad, Math.Min(box.Width, box.Height) / 2);
        Geometry result = new RectangleGeometry(box, radius, radius);
        var c = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        Vector toTip = Tip - c;
        if (!box.Contains(Tip) && toTip.Length > 0.5)
        {
            var n = new Vector(-toTip.Y, toTip.X);
            n.Normalize();
            double half = Math.Min(box.Width, box.Height) * 0.18 + 2;
            var tail = new StreamGeometry();
            using (var ctx = tail.Open())
            {
                ctx.BeginFigure(c + n * half, true, true);
                ctx.LineTo(Tip, true, true);
                ctx.LineTo(c - n * half, true, true);
            }
            result = new CombinedGeometry(GeometryCombineMode.Union, result, tail);
        }
        result.Freeze();
        return result;
    }

    public override void Render(DrawingContext dc)
    {
        dc.DrawGeometry(Frozen(new SolidColorBrush(Colors.White)), MakePen(Color, Math.Max(2, Width / 8)), Shape());
        var box = BubbleRect;
        dc.DrawText(Format(), new Point(box.X + Pad, box.Y + Pad));
    }

    public override Rect Bounds
    {
        get
        {
            var r = BubbleRect;
            r.Union(Tip);
            return Inflate(r, Math.Max(2, Width / 8));
        }
    }

    public override Annotation Offset(Vector v) => this with { Tip = Tip + v, Origin = Origin + v };

    /// <summary>The bubble stays upright, centered where it was; the tail follows its point.</summary>
    public override Annotation Transform(Matrix m, double scale)
    {
        var scaled = this with { Width = Width * scale };
        var size = scaled.BubbleRect.Size;
        return scaled with { Tip = m.Transform(Tip), Origin = (Point)CenterShift(m, BubbleRect, size) };
    }

    public override Annotation Resize(Vector d)
    {
        double height = BubbleRect.Height;
        return height <= 0 ? this : this with { Width = Math.Max(6, Width * Math.Max(2, height + d.Y) / height) };
    }

    public override bool Hit(Point p, double tolerance) => Shape().FillContains(p) || Inflate(BubbleRect, tolerance).Contains(p);
}

/// <summary>A picture placed on the capture as an object: pasted or inserted, then moved and
/// resized like any mark. Turning or mirroring the capture turns and mirrors it too.</summary>
public sealed record ImageAnnotation(BitmapSource Image, Rect Box) : Annotation(Colors.Transparent, 1)
{
    public override void Render(DrawingContext dc)
    {
        // Smooth scaling in exports as on screen; the capture's own pixels stay nearest neighbour.
        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
        using (var inner = group.Open()) inner.DrawImage(Image, Box);
        dc.DrawDrawing(group);
    }

    public override Rect Bounds => Box;

    public override Annotation Offset(Vector v) => this with { Box = new Rect(Box.TopLeft + v, Box.Size) };

    /// <summary>Keeps the picture's shape: the larger change of the two sets the new size.</summary>
    public override Annotation Resize(Vector d)
    {
        double aspect = Box.Height > 0 ? Box.Width / Box.Height : 1;
        double width = Math.Max(4, Box.Width + (Math.Abs(d.X) >= Math.Abs(d.Y * aspect) ? d.X : d.Y * aspect));
        return this with { Box = new Rect(Box.TopLeft, new Size(width, Math.Max(4, width / aspect))) };
    }

    public override Annotation Transform(Matrix m, double scale)
    {
        var box = new Rect(m.Transform(Box.TopLeft), m.Transform(Box.BottomRight));
        // The turn or mirror part of m, without its scale or shift, applied to the pixels.
        var turn = new Matrix(Math.Sign(m.M11), Math.Sign(m.M12), Math.Sign(m.M21), Math.Sign(m.M22), 0, 0);
        BitmapSource image = Image;
        if (!turn.IsIdentity)
        {
            // Turns combine with an earlier one, so repeated turns never nest a chain of bitmaps.
            var source = Image;
            if (Image is TransformedBitmap { Source: { } inner, Transform: { } earlier })
            {
                source = inner;
                turn = earlier.Value * turn;
            }
            var turned = new TransformedBitmap(source, new MatrixTransform(turn));
            turned.Freeze();
            image = turned;
        }
        return this with { Image = image, Box = box };
    }

    public override bool Hit(Point p, double tolerance) => Inflate(Box, tolerance).Contains(p);
}
