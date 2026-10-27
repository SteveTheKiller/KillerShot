using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;

namespace KillerShot.Editor;

/// <summary>Marks as versioned JSON, so a capture in the album stays editable after it closes.
/// Only the album stores this; saved and copied pictures are always flattened.</summary>
public static class MarksFormat
{
    public const int Version = 1;

    public static string Write(IEnumerable<Annotation> marks)
    {
        var list = new JsonArray();
        foreach (var mark in marks) if (ToJson(mark) is { } node) list.Add(node);
        return new JsonObject { ["v"] = Version, ["marks"] = list }.ToJsonString();
    }

    /// <summary>The complete marks, or null when any part cannot be understood safely.</summary>
    public static ImmutableList<Annotation>? Read(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["v"]?.GetValue<int>() != Version || root["marks"] is not JsonArray marks)
                return null;
            var result = ImmutableList.CreateBuilder<Annotation>();
            foreach (var node in marks)
            {
                if (node is not JsonObject o || FromJson(o) is not { } mark) return null;
                result.Add(mark);
            }
            return result.ToImmutable();
        }
        // A partial parse could omit a redaction. The caller must use the flattened output.
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException
            or NullReferenceException or NotSupportedException or ArgumentException or OverflowException or System.IO.IOException
            or System.Runtime.InteropServices.ExternalException) { return null; }
    }

    private static JsonObject? ToJson(Annotation mark) => mark switch
    {
        StrokeAnnotation s => new JsonObject
        {
            ["t"] = "stroke", ["pts"] = new JsonArray(s.Points.SelectMany(p => new[] { p.X, p.Y }).Select(v => (JsonNode)v).ToArray()),
            ["c"] = Hex(s.Color), ["w"] = s.Width, ["hl"] = s.Highlighter,
        },
        ShapeAnnotation s => new JsonObject
        {
            ["t"] = "shape", ["k"] = s.Kind.ToString(), ["ax"] = s.A.X, ["ay"] = s.A.Y, ["bx"] = s.B.X, ["by"] = s.B.Y,
            ["c"] = Hex(s.Color), ["w"] = s.Width, ["f"] = s.Filled,
        },
        TextAnnotation s => new JsonObject { ["t"] = "text", ["x"] = s.Origin.X, ["y"] = s.Origin.Y, ["s"] = s.Text, ["c"] = Hex(s.Color), ["w"] = s.Width },
        StepAnnotation s => new JsonObject { ["t"] = "step", ["x"] = s.Center.X, ["y"] = s.Center.Y, ["n"] = s.Number, ["c"] = Hex(s.Color), ["w"] = s.Width },
        CalloutAnnotation s => new JsonObject
        {
            ["t"] = "callout", ["tx"] = s.Tip.X, ["ty"] = s.Tip.Y, ["x"] = s.Origin.X, ["y"] = s.Origin.Y, ["s"] = s.Text,
            ["c"] = Hex(s.Color), ["w"] = s.Width,
        },
        ImageAnnotation s => new JsonObject
        {
            ["t"] = "image", ["x"] = s.Box.X, ["y"] = s.Box.Y, ["bw"] = s.Box.Width, ["bh"] = s.Box.Height,
            ["png"] = Convert.ToBase64String(Services.ImageOutput.EncodePng(s.Image)), ["c"] = Hex(s.Color), ["w"] = s.Width,
        },
        _ => null,
    };

    private static Annotation? FromJson(JsonObject o)
    {
        double D(string key)
        {
            double value = o[key]!.GetValue<double>();
            return double.IsFinite(value) ? value : throw new FormatException("Invalid mark coordinate.");
        }
        Color c = ParseColor(o["c"]!.GetValue<string>());
        double w = D("w");
        if (!double.IsFinite(w) || w <= 0) return null;
        switch (o["t"]?.GetValue<string>())
        {
            case "stroke":
                var values = o["pts"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
                if (values.Length < 2 || values.Length % 2 != 0 || values.Any(v => !double.IsFinite(v))) return null;
                var points = Enumerable.Range(0, values.Length / 2).Select(i => new Point(values[i * 2], values[i * 2 + 1])).ToImmutableArray();
                return new StrokeAnnotation(points, c, w, o["hl"]?.GetValue<bool>() == true);
            case "shape":
                if (!Enum.TryParse<ShapeKind>(o["k"]?.GetValue<string>(), out var kind) || !Enum.IsDefined(kind)) return null;
                return new ShapeAnnotation(kind, new Point(D("ax"), D("ay")), new Point(D("bx"), D("by")), c, w, o["f"]?.GetValue<bool>() == true);
            case "text":
                return new TextAnnotation(new Point(D("x"), D("y")), o["s"]!.GetValue<string>(), c, w);
            case "step":
                return new StepAnnotation(new Point(D("x"), D("y")), o["n"]!.GetValue<int>(), c, w);
            case "image":
                {
                    var frame = Services.ImageOutput.Load(Convert.FromBase64String(o["png"]!.GetValue<string>()));
                    double bw = D("bw"), bh = D("bh");
                    return bw > 0 && bh > 0 ? new ImageAnnotation(frame, new Rect(D("x"), D("y"), bw, bh)) : null;
                }
            case "callout":
                return new CalloutAnnotation(new Point(D("tx"), D("ty")), new Point(D("x"), D("y")), o["s"]!.GetValue<string>(), c, w);
            default:
                return null;
        }
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color ParseColor(string hex)
    {
        if (hex.Length != 9 || hex[0] != '#') throw new FormatException("Bad mark color.");
        uint v = uint.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
