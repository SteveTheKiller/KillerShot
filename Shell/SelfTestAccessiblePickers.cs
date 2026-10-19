using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static class SelfTestAccessiblePickers
{
    public static bool TryRun(string[] args)
    {
        if (SelfTestFlyoutScroll.TryRun(args)) return true;
        if (args.Length != 2 || args[0] != "--selftest-accessible-pickers") return false;
        string folder = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(folder);
        Settings.UseFolder(Path.Combine(folder, "settings"));
        App.InitializeKit();
        LocaleManager.Apply(Locale.EnUS);
        _ = RunAsync(folder);
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>();
        try
        {
            foreach (Theme theme in new[] { Theme.Black, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                ThemeManager.ApplyAccent(theme, Accent.Teal);
                var dialog = ShowColorPicker();
                await Task.Delay(250);
                Render((FrameworkElement)dialog.Content, Path.Combine(folder, "saved-colors-" + theme + ".png"));
                dialog.Close();
            }
            await CheckSavedColors();
            report.Add("Saved colors, Replace and Reset expose keyboard and automation operations PASS");
            await CheckCloseGlyph();
            report.Add("Dialog Close responds to automation and honors the window closing guard PASS");
            await CheckThemeAccents(folder);
            report.Add("All eight theme accents expose named keyboard selection and selected state in Black and 98SE PASS");
            code = 0;
        }
        catch (Exception ex) { report.Add(ex.ToString()); }
        finally
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().ToArray()) window.Close();
            File.WriteAllLines(Path.Combine(folder, "accessible-pickers-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static ColorPickerDialog ShowColorPicker()
    {
        var dialog = new ColorPickerDialog(null, Colors.Cyan)
        {
            Left = -20000, Top = -20000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        dialog.Show();
        return dialog;
    }

    private static async Task CheckSavedColors()
    {
        ThemeManager.Apply(Theme.Black);
        var dialog = ShowColorPicker();
        await Task.Delay(100);
        var replace = Descendants<FrameworkElement>(dialog).Single(e => e.ToolTip?.ToString() == LocaleManager.Loc("Str_TT_ReplaceSwatch"));
        var replacePeer = RequirePeer(replace, "Replace");
        if (replacePeer.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
            throw new InvalidOperationException("Replace does not expose its armed state as a toggle.");
        toggle.Toggle();
        await Task.Delay(30);
        if (toggle.ToggleState != ToggleState.On) throw new InvalidOperationException("Replace did not enter replacement mode.");
        var swatch = SavedSwatches(dialog).First();
        var select = RequirePeer(swatch, "Saved color").GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
            ?? throw new InvalidOperationException("Saved colors have no selection operation.");
        select.Select();
        await Task.Delay(30);
        if (ColorPickerDialog.UserSwatches()[0] != Colors.Cyan || toggle.ToggleState != ToggleState.Off)
            throw new InvalidOperationException("Replacing a saved color through automation did not persist Cyan and exit replacement mode.");
        Color? observed = null;
        dialog.ColorChanged += color => observed = color;
        var second = SavedSwatches(dialog).Skip(1).First();
        var secondSelect = (ISelectionItemProvider)RequirePeer(second, "Saved color").GetPattern(PatternInterface.SelectionItem)!;
        secondSelect.Select();
        await Task.Delay(30);
        if (observed != Color.FromRgb(0xE8, 0x96, 0x2C) || !secondSelect.IsSelected)
            throw new InvalidOperationException("Selecting the second saved color did not select Orange and expose its selected state.");
        var reset = Descendants<FrameworkElement>(dialog).Single(e => e.ToolTip?.ToString() == LocaleManager.Loc("Str_TT_ResetSwatches"));
        if (RequirePeer(reset, "Reset").GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException("Reset has no invoke operation.");
        invoke.Invoke();
        await Task.Delay(30);
        if (ColorPickerDialog.UserSwatches()[0] != Color.FromRgb(0xDD, 0x50, 0x4B))
            throw new InvalidOperationException("Reset did not restore the default saved colors.");
        dialog.Close();
    }

    private static IEnumerable<FrameworkElement> SavedSwatches(DependencyObject root) =>
        Descendants<FrameworkElement>(root).Where(e => e.ToolTip?.ToString() is string text
            && (text == LocaleManager.Loc("Str_TT_SwatchUse") || text == LocaleManager.Loc("Str_TT_SwatchReplace")));

    private static async Task CheckCloseGlyph()
    {
        var window = new Window
        {
            Width = 320, Height = 100, Left = -20000, Top = -20000, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        bool closed = false, allowClose = true;
        window.Closed += (_, _) => closed = true;
        window.Closing += (_, e) => e.Cancel = !allowClose;
        var close = DialogChrome.CloseGlyph(LocaleManager.Loc("Str_Sys_Close"), window.Close);
        window.Content = new Grid { Children = { close } };
        window.Show();
        var invoke = RequirePeer(close, "Dialog Close").GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("Dialog Close has no invoke operation.");
        allowClose = false;
        invoke.Invoke();
        await Task.Delay(30);
        if (closed) throw new InvalidOperationException("Dialog Close bypassed the window closing guard.");
        allowClose = true;
        invoke.Invoke();
        await Task.Delay(30);
        if (!closed) throw new InvalidOperationException("Dialog Close ignores automation invocation.");
    }

    private static async Task CheckThemeAccents(string folder)
    {
        Exception? failure = null;
        foreach (Theme theme in new[] { Theme.Black, Theme.SE98 })
        {
            ThemeManager.Apply(theme);
            ThemeManager.ApplyAccent(theme, Accent.Teal);
            var button = new Button { Content = "Theme" };
            var window = new Window
            {
                Width = 210, Height = 520, Left = -20000, Top = -20000, ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Content = button,
            };
            window.Show();
            var menu = new ContextMenu { ItemContainerStyle = (Style)Application.Current.FindResource("PanelMenuItem") };
            var picker = new ThemePicker(window, menu, button);
            try
            {
                picker.Toggle();
                menu.IsOpen = false;
                var panel = (Grid)menu.Items[0];
                menu.Items.Clear();
                window.Content = panel;
                await Task.Delay(250);
                Render(panel, Path.Combine(folder, "theme-accents-" + theme + ".png"));
                var options = Descendants<FrameworkElement>(panel).Where(e => e.Tag is Accent).ToArray();
                if (options.Length != 8) throw new InvalidOperationException("The theme picker must offer all eight accents.");
                foreach (var option in options)
                {
                    var selection = RequirePeer(option, theme + " accent " + option.Tag).GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                        ?? throw new InvalidOperationException("Theme accents have no selection operation.");
                    selection.Select();
                    await Task.Delay(220);
                    if (ThemeManager.AccentChoiceFor(theme) != (Accent)option.Tag || !selection.IsSelected)
                        throw new InvalidOperationException("Selecting the accent did not update the palette and selected state.");
                    if (options.Count(e => ((ISelectionItemProvider)RequirePeer(e, "Accent").GetPattern(PatternInterface.SelectionItem)!).IsSelected) != 1)
                        throw new InvalidOperationException("Exactly one theme accent must be selected.");
                }
            }
            catch (Exception ex) { failure ??= ex; }
            finally { menu.IsOpen = false; window.Close(); }
        }
        if (failure != null) throw failure;
    }

    private static AutomationPeer RequirePeer(FrameworkElement element, string description)
    {
        if (element is not Control { Focusable: true, IsTabStop: true } control)
            throw new InvalidOperationException(description + " is not a keyboard-operable control.");
        var peer = UIElementAutomationPeer.CreatePeerForElement(control)
            ?? throw new InvalidOperationException(description + " has no automation peer.");
        if (string.IsNullOrWhiteSpace(peer.GetName())) throw new InvalidOperationException(description + " has no accessible name.");
        return peer;
    }

    private static void Render(FrameworkElement root, string path)
    {
        root.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        ImageOutput.SavePng(image, path);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
