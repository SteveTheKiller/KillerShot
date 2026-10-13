using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KillerShot.Editor;

public enum ToolKind { Select, Pen, Highlighter, Arrow, Rectangle, Ellipse, Text, Redact, Crop }

/// <summary>One mark on the capture, in image pixel coordinates. Annotations are immutable so an
/// undo step is just the previous list.</summary>
public abstract record Annotation(Color Color, double Width)
{
    public abstract void Render(DrawingContext dc);
    public abstract Rect Bounds { get; }
    public abstract Annotation Offset(Vector v);
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

    public override bool Hit(Point p, double tolerance) =>
        Geometry.StrokeContains(MakePen(Colors.Black, Width + tolerance * 2), p);
}

public enum ShapeKind { Arrow, Rectangle, Ellipse, Redact }

/// <summary>Arrow, rectangle, ellipse or redaction box between two points.</summary>
public sealed record ShapeAnnotation(ShapeKind Kind, Point A, Point B, Color Color, double Width)
    : Annotation(Color, Width)
{
    public Rect Box => new(A, B);

    public override void Render(DrawingContext dc)
    {
        switch (Kind)
        {
            case ShapeKind.Rectangle:
                dc.DrawRectangle(null, MakePen(Color, Width, PenLineCap.Square), Box);
                break;
            case ShapeKind.Ellipse:
                var b = Box;
                dc.DrawEllipse(null, MakePen(Color, Width), new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                break;
            case ShapeKind.Redact:
                // Fully opaque, so the exported pixels underneath are replaced, never tinted.
                dc.DrawRectangle(Frozen(new SolidColorBrush(Colors.Black)), null, Box);
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
            var r = Box;
            if (Kind != ShapeKind.Redact) r = Inflate(r, Kind == ShapeKind.Arrow ? Math.Max(10, Width * 4) : Width / 2);
            return r;
        }
    }

    public override Annotation Offset(Vector v) => this with { A = A + v, B = B + v };

    public override bool Hit(Point p, double tolerance)
    {
        if (Kind == ShapeKind.Redact) return Inflate(Box, tolerance).Contains(p);
        if (Kind == ShapeKind.Arrow)
            return ArrowGeometry().StrokeContains(MakePen(Colors.Black, Width + tolerance * 2), p) || ArrowGeometry().FillContains(p);
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
}
