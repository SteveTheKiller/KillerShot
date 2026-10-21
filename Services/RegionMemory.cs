using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using KillerShot.Capture;
using KillerShot.Native;

namespace KillerShot.Services;

/// <summary>A saved capture area: a fixed place on the desktop, or a fixed size captured around
/// the pointer. Coordinates are physical desktop pixels.</summary>
internal sealed record RegionPreset(string Name, Win32.RECT? Place, int Width, int Height)
{
    public bool IsFixedSize => Place == null;
}

/// <summary>The last region and the saved regions, with the monitor layout each was made on, so a
/// repeat never captures a stale area after a display was added, moved or rescaled.</summary>
internal static class RegionMemory
{
    /// <summary>The monitor layout as text: every monitor's bounds, primary marked.</summary>
    public static string Layout(IReadOnlyList<MonitorBounds> monitors) =>
        string.Join(";", monitors.OrderBy(m => m.Bounds.Left).ThenBy(m => m.Bounds.Top)
            .Select(m => $"{m.Bounds.Left},{m.Bounds.Top},{m.Bounds.Width},{m.Bounds.Height}{(m.Primary ? "p" : "")}"));

    public static void Remember(Win32.RECT region, IReadOnlyList<MonitorBounds> monitors)
    {
        if (region.IsEmpty) return;
        Settings.Set("LastRegion", string.Create(CultureInfo.InvariantCulture,
            $"{region.Left},{region.Top},{region.Width},{region.Height}|{Layout(monitors)}"));
    }

    /// <summary>The last region, when it was taken on the current monitor layout.</summary>
    public static bool TryLast(IReadOnlyList<MonitorBounds> monitors, out Win32.RECT region)
    {
        region = default;
        if (Settings.Get("LastRegion") is not { } saved || saved.Split('|') is not [var rect, var layout]) return false;
        if (layout != Layout(monitors) || !TryRect(rect, out region)) return false;
        return OnDesktop(region, monitors);
    }

    public static bool HasLast => Settings.Get("LastRegion") is { Length: > 0 };

    private static bool TryRect(string text, out Win32.RECT rect)
    {
        rect = default;
        var parts = text.Split(',');
        if (parts.Length != 4 || !parts.All(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))) return false;
        int[] v = parts.Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        if (v[2] < 1 || v[3] < 1) return false;
        rect = new Win32.RECT(v[0], v[1], v[0] + v[2], v[1] + v[3]);
        return true;
    }

    /// <summary>A region lies on the desktop when every corner is on some monitor.</summary>
    private static bool OnDesktop(Win32.RECT r, IReadOnlyList<MonitorBounds> monitors)
    {
        Win32.POINT[] corners = [new() { X = r.Left, Y = r.Top }, new() { X = r.Right - 1, Y = r.Top },
            new() { X = r.Left, Y = r.Bottom - 1 }, new() { X = r.Right - 1, Y = r.Bottom - 1 }];
        return corners.All(c => monitors.Any(m => m.Bounds.Contains(c)));
    }

    // ---- saved regions ----

    public const int MaxPresets = 12;

    public static IReadOnlyList<RegionPreset> Presets
    {
        get
        {
            try
            {
                if (Settings.Get("RegionPresets") is not { } json || JsonNode.Parse(json) is not JsonArray list) return [];
                var result = new List<RegionPreset>();
                foreach (var node in list.OfType<JsonObject>())
                {
                    string name = node["n"]?.GetValue<string>() ?? "";
                    int w = node["w"]?.GetValue<int>() ?? 0, h = node["h"]?.GetValue<int>() ?? 0;
                    if (name.Length == 0 || w < 1 || h < 1) continue;
                    Win32.RECT? place = node["x"] is { } x && node["y"] is { } y
                        ? new Win32.RECT(x.GetValue<int>(), y.GetValue<int>(), x.GetValue<int>() + w, y.GetValue<int>() + h) : null;
                    result.Add(new RegionPreset(name, place, w, h));
                }
                return result;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return []; }
        }
    }

    /// <summary>Adds or replaces a saved region by name, keeping at most <see cref="MaxPresets"/>.</summary>
    public static void SavePreset(RegionPreset preset)
    {
        var list = Presets.Where(p => !p.Name.Equals(preset.Name, StringComparison.CurrentCultureIgnoreCase)).Append(preset)
            .TakeLast(MaxPresets).ToList();
        Store(list);
    }

    public static void RemovePreset(string name) =>
        Store(Presets.Where(p => !p.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)).ToList());

    private static void Store(IEnumerable<RegionPreset> presets)
    {
        var list = new JsonArray();
        foreach (var p in presets)
        {
            var node = new JsonObject { ["n"] = p.Name, ["w"] = p.Width, ["h"] = p.Height };
            if (p.Place is { } place) { node["x"] = place.Left; node["y"] = place.Top; }
            list.Add(node);
        }
        Settings.Set("RegionPresets", list.ToJsonString());
    }

    /// <summary>Where a saved region captures now: its place, or its size centered on the pointer
    /// and kept inside the pointer's monitor. False when a fixed place is off the desktop.</summary>
    public static bool Resolve(RegionPreset preset, Win32.POINT pointer, IReadOnlyList<MonitorBounds> monitors, out Win32.RECT region)
    {
        if (preset.Place is { } place)
        {
            region = place;
            return OnDesktop(place, monitors);
        }
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(pointer))?.Bounds ?? monitors[0].Bounds;
        int w = Math.Min(preset.Width, monitor.Width), h = Math.Min(preset.Height, monitor.Height);
        int left = Math.Clamp(pointer.X - w / 2, monitor.Left, monitor.Right - w);
        int top = Math.Clamp(pointer.Y - h / 2, monitor.Top, monitor.Bottom - h);
        region = new Win32.RECT(left, top, left + w, top + h);
        return true;
    }
}
