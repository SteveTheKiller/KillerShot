namespace KillerTools.Qr;

/// <summary>A projective map between two quadrilaterals (as in ZXing's PerspectiveTransform, written out fresh).</summary>
internal readonly struct Perspective
{
    private readonly double a11, a12, a13, a21, a22, a23, a31, a32, a33;

    private Perspective(double a11, double a21, double a31, double a12, double a22, double a32, double a13, double a23, double a33)
    {
        this.a11 = a11; this.a12 = a12; this.a13 = a13;
        this.a21 = a21; this.a22 = a22; this.a23 = a23;
        this.a31 = a31; this.a32 = a32; this.a33 = a33;
    }

    /// <summary>Maps (x0,y0)..(x3,y3) onto (u0,v0)..(u3,v3).</summary>
    public static Perspective Between(QrPoint[] from, QrPoint[] to) => ToQuad(to).Times(FromQuad(from));

    public QrPoint Map(double x, double y)
    {
        double d = a13 * x + a23 * y + a33;
        return new QrPoint((a11 * x + a21 * y + a31) / d, (a12 * x + a22 * y + a32) / d);
    }

    private static Perspective ToQuad(QrPoint[] q)
    {
        double x0 = q[0].X, y0 = q[0].Y, x1 = q[1].X, y1 = q[1].Y, x2 = q[2].X, y2 = q[2].Y, x3 = q[3].X, y3 = q[3].Y;
        double dx3 = x0 - x1 + x2 - x3, dy3 = y0 - y1 + y2 - y3;
        if (Math.Abs(dx3) < 1e-12 && Math.Abs(dy3) < 1e-12)
            return new Perspective(x1 - x0, x2 - x1, x0, y1 - y0, y2 - y1, y0, 0, 0, 1);
        double dx1 = x1 - x2, dx2 = x3 - x2, dy1 = y1 - y2, dy2 = y3 - y2;
        double den = dx1 * dy2 - dx2 * dy1;
        double a13 = (dx3 * dy2 - dx2 * dy3) / den, a23 = (dx1 * dy3 - dx3 * dy1) / den;
        return new Perspective(x1 - x0 + a13 * x1, x3 - x0 + a23 * x3, x0, y1 - y0 + a13 * y1, y3 - y0 + a23 * y3, y0, a13, a23, 1);
    }

    private static Perspective FromQuad(QrPoint[] q) => ToQuad(q).Adjoint();

    private Perspective Adjoint() => new(
        a22 * a33 - a23 * a32, a23 * a31 - a21 * a33, a21 * a32 - a22 * a31,
        a13 * a32 - a12 * a33, a11 * a33 - a13 * a31, a12 * a31 - a11 * a32,
        a12 * a23 - a13 * a22, a13 * a21 - a11 * a23, a11 * a22 - a12 * a21);

    private Perspective Times(Perspective o) => new(
        a11 * o.a11 + a21 * o.a12 + a31 * o.a13, a11 * o.a21 + a21 * o.a22 + a31 * o.a23, a11 * o.a31 + a21 * o.a32 + a31 * o.a33,
        a12 * o.a11 + a22 * o.a12 + a32 * o.a13, a12 * o.a21 + a22 * o.a22 + a32 * o.a23, a12 * o.a31 + a22 * o.a32 + a32 * o.a33,
        a13 * o.a11 + a23 * o.a12 + a33 * o.a13, a13 * o.a21 + a23 * o.a22 + a33 * o.a23, a13 * o.a31 + a23 * o.a32 + a33 * o.a33);
}
