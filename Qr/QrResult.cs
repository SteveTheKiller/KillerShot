namespace KillerTools.Qr;

/// <summary>A point in image pixel coordinates.</summary>
public readonly record struct QrPoint(double X, double Y);

/// <summary>The error correction level a code was made with.</summary>
public enum QrErrorCorrection { L, M, Q, H }

/// <summary>One code read from an image.</summary>
/// <param name="Text">The decoded text.</param>
/// <param name="Version">The symbol version, 1 to 40 (21 to 177 modules a side).</param>
/// <param name="ErrorCorrection">The level the code was made with.</param>
/// <param name="Corners">The code's outer corners in the image: top-left, top-right, bottom-right, bottom-left as the code reads.</param>
public sealed record QrResult(string Text, int Version, QrErrorCorrection ErrorCorrection, IReadOnlyList<QrPoint> Corners);
