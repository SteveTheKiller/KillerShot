using System.Globalization;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShot.Capture;
using KillerShot.Controls;
using KillerShot.Native;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>Repeating the last region, saved regions, and captures on a timer.</summary>
internal sealed partial class CaptureController
{
    /// <summary>The last region again, without selecting. When the monitors changed since, the
    /// selector opens instead, so a stale area is never captured.</summary>
    public Task RepeatRegion(bool fromShortcut = false)
    {
        if (RegionMemory.TryLast(FrozenDesktop.EnumerateMonitors(), out var region))
            return RunAsync(fromShortcut ? ShortcutAction.RepeatRegion : null, CaptureMode.Region, IntPtr.Zero, fixedRegion: region);
        if (RegionMemory.HasLast) _tray.ShowInfo(Loc("Str_Region_Repeat"), Loc("Str_Region_LayoutChanged"));
        return RunAsync(fromShortcut ? ShortcutAction.LastMode : null, CaptureMode.Region, IntPtr.Zero);
    }

    public Task CapturePreset(RegionPreset preset)
    {
        Win32.GetCursorPos(out var pointer);
        if (!RegionMemory.Resolve(preset, pointer, FrozenDesktop.EnumerateMonitors(), out var region))
        {
            _tray.ShowWarning(preset.Name, Loc("Str_Region_Offscreen"));
            return Task.CompletedTask;
        }
        return RunAsync(null, CaptureMode.Region, IntPtr.Zero, fixedRegion: region);
    }

    // ---- interval ----

    private DispatcherTimer? _interval;
    private int _intervalTaken, _intervalLimit;
    private Win32.RECT _intervalRegion;

    public bool IntervalRunning => _interval != null;
    public int IntervalTaken => _intervalTaken;

    /// <summary>Raised when interval capture starts, takes a picture or stops.</summary>
    public event Action? IntervalChanged;

    /// <summary>Captures the last region every <paramref name="seconds"/> seconds until stopped, or
    /// <paramref name="count"/> times when it is above zero. False when there is no usable region.</summary>
    public bool StartInterval(int seconds, int count = 0)
    {
        StopInterval();
        if (!RegionMemory.TryLast(FrozenDesktop.EnumerateMonitors(), out _intervalRegion))
        {
            _tray.ShowInfo(Loc("Str_Region_Interval"), Loc(RegionMemory.HasLast ? "Str_Region_LayoutChanged" : "Str_Region_NeedRegion"));
            return false;
        }
        _intervalTaken = 0;
        _intervalLimit = Math.Max(0, count);
        CaptureSettings.IntervalSeconds = seconds;
        _interval = new DispatcherTimer(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 3600)), DispatcherPriority.Normal, (_, _) => IntervalTick(), Dispatcher.CurrentDispatcher);
        _interval.Start();
        _tray.ShowInfo(Loc("Str_Region_Interval"), string.Format(CultureInfo.CurrentCulture, Loc("Str_Region_IntervalStarted"), seconds));
        IntervalChanged?.Invoke();
        IntervalTick();   // the first picture right away
        return true;
    }

    public void StopInterval()
    {
        if (_interval == null) return;
        _interval.Stop();
        _interval = null;
        _tray.ShowInfo(Loc("Str_Region_Interval"), string.Format(CultureInfo.CurrentCulture, Loc("Str_Region_IntervalStopped"), _intervalTaken));
        IntervalChanged?.Invoke();
    }

    /// <summary>The interval shortcut and tray command: start with the saved interval, or stop.</summary>
    public void ToggleInterval()
    {
        if (IntervalRunning) StopInterval();
        else StartInterval(CaptureSettings.IntervalSeconds);
    }

    private void IntervalTick()
    {
        // A tick that lands while another capture runs is skipped, never queued.
        if (_interval == null || _busy) return;
        // A display added, moved or rescaled since the start would make the region stale: stop.
        if (!RegionMemory.TryLast(FrozenDesktop.EnumerateMonitors(), out var current) || !current.Equals(_intervalRegion))
        {
            _tray.ShowInfo(Loc("Str_Region_Interval"), Loc("Str_Region_LayoutChanged"));
            StopInterval();
            return;
        }
        _ = RunAsync(null, CaptureMode.Region, IntPtr.Zero, fixedRegion: _intervalRegion, interval: true);
    }

    private void IntervalCaptured()
    {
        _intervalTaken++;
        IntervalChanged?.Invoke();
        if (_intervalLimit > 0 && _intervalTaken >= _intervalLimit) StopInterval();
    }

    // ---- menus ----

    /// <summary>Repeat, saved regions and interval commands for the tray and the editor's capture menu.</summary>
    public IEnumerable<object> RegionMenuItems(Func<ShortcutAction, string> gesture)
    {
        var repeat = new MenuItem { Header = Loc("Str_Region_Repeat"), InputGestureText = gesture(ShortcutAction.RepeatRegion), IsEnabled = RegionMemory.HasLast };
        repeat.Click += (_, _) => Defer(() => _ = RepeatRegion());
        yield return repeat;

        var scrolling = new MenuItem { Header = Loc("Str_Scroll_Title") };
        scrolling.Click += (_, _) => Defer(() => _ = ScrollingCapture());
        yield return scrolling;

        var saved = new MenuItem { Header = Loc("Str_Region_Saved") };
        foreach (var preset in RegionMemory.Presets)
        {
            var p = preset;
            string detail = p.IsFixedSize ? $"{p.Width} x {p.Height}" : $"{p.Width} x {p.Height} @ {p.Place!.Value.Left}, {p.Place.Value.Top}";
            var item = new MenuItem { Header = p.Name, InputGestureText = detail };
            item.Click += (_, _) => Defer(() => _ = CapturePreset(p));
            saved.Items.Add(item);
        }
        if (saved.Items.Count > 0) saved.Items.Add(new Separator());
        var saveLast = new MenuItem { Header = Loc("Str_Region_SaveLast"), IsEnabled = RegionMemory.HasLast };
        saveLast.Click += (_, _) => SaveLastRegion();
        saved.Items.Add(saveLast);
        var fixedSize = new MenuItem { Header = Loc("Str_Region_AddSize") };
        fixedSize.Click += (_, _) => AddFixedSize();
        saved.Items.Add(fixedSize);
        if (RegionMemory.Presets.Count > 0)
        {
            var remove = new MenuItem { Header = Loc("Str_Region_Remove") };
            foreach (var preset in RegionMemory.Presets)
            {
                string name = preset.Name;
                var item = new MenuItem { Header = name };
                item.Click += (_, _) => RegionMemory.RemovePreset(name);
                remove.Items.Add(item);
            }
            saved.Items.Add(remove);
        }
        yield return saved;

        var interval = new MenuItem { Header = Loc("Str_Region_Interval"), InputGestureText = gesture(ShortcutAction.Interval) };
        if (IntervalRunning)
        {
            var stop = new MenuItem { Header = string.Format(CultureInfo.CurrentCulture, Loc("Str_Region_IntervalStop"), _intervalTaken) };
            stop.Click += (_, _) => StopInterval();
            interval.Items.Add(stop);
        }
        else
            foreach (int seconds in new[] { 5, 10, 30, 60, 300 })
            {
                int s = seconds;
                var every = new MenuItem
                {
                    Header = string.Format(CultureInfo.CurrentCulture, Loc("Str_Region_Every"), s),
                    IsCheckable = true, IsChecked = CaptureSettings.IntervalSeconds == s, IsEnabled = RegionMemory.HasLast,
                };
                every.Click += (_, _) => StartInterval(s);
                interval.Items.Add(every);
            }
        yield return interval;
    }

    /// <summary>Menu-started captures wait for the menu to fade so it is not in the picture.</summary>
    private static void Defer(Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private void SaveLastRegion()
    {
        if (!RegionMemory.TryLast(FrozenDesktop.EnumerateMonitors(), out var region)) return;
        var dialog = new PromptDialog(Loc("Str_Region_SaveLast"), $"{region.Width} x {region.Height} @ {region.Left}, {region.Top}", Loc("Str_Lib_Save"),
            string.Format(CultureInfo.CurrentCulture, Loc("Str_Region_DefaultName"), region.Width, region.Height));
        if (dialog.ShowDialog() != true || dialog.Text.Trim() is not { Length: > 0 } name) return;
        RegionMemory.SavePreset(new RegionPreset(name, region, region.Width, region.Height));
    }

    private void AddFixedSize()
    {
        var dialog = new PromptDialog(Loc("Str_Region_AddSize"), Loc("Str_Region_AddSizeDetail"), Loc("Str_Lib_Save"), "1280 x 720");
        if (dialog.ShowDialog() != true) return;
        var match = System.Text.RegularExpressions.Regex.Match(dialog.Text.Trim(), @"^(\d{1,5})\s*[xX×*,]\s*(\d{1,5})$");
        if (!match.Success || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is not (>= 8 and int w)
            || int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) is not (>= 8 and int h))
        {
            _tray.ShowWarning(Loc("Str_Region_AddSize"), Loc("Str_Img_Invalid"));
            return;
        }
        RegionMemory.SavePreset(new RegionPreset($"{w} x {h}", null, w, h));
    }
}
