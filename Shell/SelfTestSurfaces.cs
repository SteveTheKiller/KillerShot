using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

internal static partial class SelfTestChrome
{
    private static void CheckSurfaces(MainWindow window, Theme theme, Accent accent, string folder, Action<bool, string> check)
    {
        string state = $"{theme}/{accent}";
        var pane = (Border)window.FindName("ContentPane");
        var frame = (Border)window.FindName("EditorFrame");
        check(pane.Effect == null, state + " pane content has no rasterizing effect");
        var shadow = (Border)window.FindName("ContentPaneShadow");
        check(ReferenceEquals(shadow.Effect, Application.Current.TryFindResource("PaneShadowEffect")),
            state + " separate pane shadow follows the current theme");
        check(theme != Theme.SE98 || frame.Effect == null, state + " 98SE clears the image shadow");
        if (theme == Theme.SE98)
            check(Application.Current.TryFindResource("PaneShadowEffect") == null,
                state + " 98SE clears pane effects");

        window.UpdateLayout();
        var root = (FrameworkElement)window.Content;
        var footer = Descendants<Border>(root).First(b => Grid.GetRow(b) == 2
            && ReferenceEquals(b.Background, Application.Current.FindResource("FooterBackgroundBrush")));
        check(Math.Abs(footer.ActualHeight - 24) < 0.1, state + " footer remains 24px");
        var before = Pixels(root);
        var effects = Descendants<UIElement>(root).Where(e => e.Effect is System.Windows.Media.Effects.DropShadowEffect)
            .Select(e => (Element: e, Effect: e.Effect)).ToArray();
        foreach (var entry in effects) entry.Element.SetCurrentValue(UIElement.EffectProperty, null);
        var after = Pixels(root);
        foreach (var entry in effects) entry.Element.SetCurrentValue(UIElement.EffectProperty, entry.Effect);
        var bounds = footer.TransformToAncestor(root).TransformBounds(new Rect(footer.RenderSize));
        int stride = (int)Math.Ceiling(root.ActualWidth) * 4;
        bool same = true;
        for (int y = (int)Math.Ceiling(bounds.Top); y < Math.Floor(bounds.Bottom) && same; y++)
            for (int x = (int)Math.Ceiling(bounds.Left) * 4; x < Math.Floor(bounds.Right) * 4; x++)
                if (before[y * stride + x] != after[y * stride + x]) { same = false; break; }
        check(same, state + " pane shadows do not paint over the footer");

        var menu = new MenuItem { Header = "Menu command", InputGestureText = "Ctrl+S", IsCheckable = true, IsChecked = true };
        var button = new Button { Style = (Style)Application.Current.FindResource("ToolbarButton"), Content = "Toolbar", Width = 100, Height = 30 };
        var stack = new StackPanel { Width = 280, Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush") };
        stack.Children.Add(menu); stack.Children.Add(button);
        stack.Measure(new Size(280, 100)); stack.Arrange(new Rect(0, 0, 280, 100)); stack.UpdateLayout();
        typeof(MenuItem).GetProperty(nameof(MenuItem.IsHighlighted))!.GetSetMethod(true)!.Invoke(menu, new object[] { true });
        menu.UpdateLayout();
        string highlight = Application.Current.FindResource("ComboHighlightTextBrush").ToString()!;
        check(menu.Foreground.ToString() == highlight, state + " highlighted menu label uses its paired text color");
        foreach (string name in new[] { "Gesture", "SubArrow", "Check" })
            check(((TextBlock)menu.Template.FindName(name, menu)).Foreground.ToString() == highlight,
                state + " highlighted menu " + name + " uses its paired text color");
        var icon = (DependencyObject)menu.Template.FindName("IconPresenter", menu);
        check(((Brush)icon.GetValue(TextBlock.ForegroundProperty)).ToString() == highlight,
            state + " highlighted menu icon uses its paired text color");
        check(((Border)menu.Template.FindName("bg", menu)).Background.ToString() == Application.Current.FindResource("MenuHoverBrush").ToString(),
            state + " highlighted menu uses the menu hover surface");

        var mouse = (DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var pressed = (DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        button.SetValue(mouse, true); button.UpdateLayout();
        var face = (Border)button.Template.FindName("Bd", button);
        check(face.Background.ToString() == Application.Current.FindResource("RowHoverBrush").ToString(), state + " toolbar hover uses its neutral surface");
        button.SetValue(pressed, true); button.UpdateLayout();
        check(face.Background.ToString() == Application.Current.FindResource("SurfaceBrush").ToString(), state + " toolbar press uses its pressed surface");
        button.SetValue(pressed, false); button.CommandParameter = "selected"; button.UpdateLayout();
        check(face.Background.ToString() == Application.Current.FindResource("CalendarViewSelectedBrush").ToString(), state + " selected toolbar hover retains its selection surface");
        // Real view buttons supply their selection foreground through FooterView's content binding.
        if (accent == Accent.Green && theme is Theme.Dark or Theme.Light or Theme.SE98)
        {
            Save(stack, System.IO.Path.Combine(folder, theme + "-interaction-states.png"));
            Save(root, System.IO.Path.Combine(folder, theme + "-pane-footer.png"));
        }

        var style = Application.Current.TryFindResource("ShadowListBox") as Style;
        check(style != null, state + " dialog list has the family scroll-cue template");
        if (style == null) return;
        var list = new ListBox { Style = style, Width = 200, Height = 100,
            Background = (Brush)Application.Current.FindResource("PaneBrush"),
            Foreground = (Brush)Application.Current.FindResource("TextBrush") };
        for (int i = 0; i < 50; i++) list.Items.Add("File " + i);
        list.Measure(new Size(200, 100)); list.Arrange(new Rect(0, 0, 200, 100)); list.UpdateLayout();
        var scroller = Descendants<ScrollViewer>(list).First();
        list.SelectedIndex = 12;
        var cues = Descendants<Border>(scroller).Where(b => b.Style == Application.Current.TryFindResource("ScrollTopCue")
            || b.Style == Application.Current.TryFindResource("ScrollBottomCue")).ToArray();
        check(cues.Length == 2, state + " dialog list has top and bottom cues");
        if (cues.Length == 2)
        {
            check(cues[0].Visibility == Visibility.Collapsed && cues[1].Visibility == Visibility.Visible,
                state + " dialog cues show only the available direction at the start");
            scroller.ScrollToEnd(); list.UpdateLayout();
            check(cues[0].Visibility == Visibility.Visible && cues[1].Visibility == Visibility.Collapsed,
                state + " dialog cues show only the available direction at the end");
            check(cues.All(b => !b.IsHitTestVisible && (theme != Theme.SE98 || b.Opacity == 0)),
                state + " cues preserve input and disappear in 98SE");
            check(list.SelectedIndex == 12, state + " scrolling preserves selection");
            check(cues.All(b => Grid.GetColumnSpan(b) == 2), state + " vertical cues span the scrollbar lane");
            if (accent == Accent.Green && theme is Theme.Dark or Theme.Light or Theme.SE98)
                Save(list, System.IO.Path.Combine(folder, theme + "-dialog-list.png"));
        }
        foreach (string name in new[] { "EditorScroll", "CaptureSettingsScroll", "VideoSettingsScroll", "KsListHostScroll" })
            check(ReferenceEquals(((ScrollViewer)window.FindName(name)).Style, Application.Current.FindResource("ShadowScrollViewer")),
                state + " " + name + " uses the verified scroll-cue template");
        var picker = new FileDialog();
        check(ReferenceEquals(((ListBox)picker.FindName("FileList")).Style, style), state + " file picker uses the verified scroll-cue template");
        picker.Close();
    }

    private static byte[] Pixels(FrameworkElement root)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
