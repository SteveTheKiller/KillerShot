using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using KillerShot.Services;
using KillerShot.Shell;

namespace KillerShot.Controls
{
    /// <summary>
    /// Canonical KillerPDF-style theme picker: a radio-row ContextMenu listing all thirteen themes,
    /// with ONE vertical accent strip beside the list, repainted for whichever accent-capable
    /// family (Dark, Light, Black, 98SE) is selected, and the Film Grain switch under both columns.
    /// </summary>
    internal sealed class ThemePicker
    {
        private static readonly Theme[] Themes =
        [
            Theme.Dark, Theme.Light, Theme.Black, Theme.SE98, Theme.Blood, Theme.Greed,
            Theme.Cyanotic, Theme.Ectoplasm, Theme.Decay, Theme.Malaise, Theme.Sepulchre,
            Theme.Delirium, Theme.Mourning
        ];

        private const double AccentStripWidth = 39;
        private const double AccentSlideMs = 170;

        private readonly Window _window;
        private readonly ContextMenu _menu;
        private readonly FrameworkElement _button;
        private readonly FrameworkElement _pane;
        private Grid? _accentHost;
        private readonly List<Border> _accentDots = [];
        private CheckBox? _filmGrainCheck;
        // True while this picker is the one changing the theme, so ThemeChanged does not rebuild
        // the menu out from under the accent strip's slide.
        private bool _applying;

        internal ThemePicker(Window window, ContextMenu menu, FrameworkElement button,
                             FrameworkElement contentPane)
        {
            _window = window;
            _menu = menu;
            _button = button;
            _pane = contentPane;
            if (_button is FrameworkElement themeButton)
                themeButton.ContextMenuOpening += (_, e) => { e.Handled = true; Toggle(); };
        }

        internal void Toggle()
        {
            if (_menu.IsOpen) { _menu.IsOpen = false; return; }
            Build();
            FlyoutPlacement.UsePane(_pane);
            FlyoutPlacement.Attach(_menu, _button);
            _menu.IsOpen = true;
            _menu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
        }

        internal void Refresh()
        {
            if (_menu.IsOpen && !_applying) Build();
        }

        private void Build()
        {
            _menu.Items.Clear();
            _accentHost = null;
            _accentDots.Clear();
            // Hover rows sit 4px in from both menu edges: 12 left less the row's -8, and 2 right
            // plus the row's own 2. The theme column fills the menu (*), so when the menu is wider
            // than the list the hover still reaches across instead of stopping short.
            var panel = new Grid { Margin = new Thickness(12, 10, 2, 10) };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var list = new StackPanel { MinWidth = 120 };
            Grid.SetColumn(list, 0);
            panel.Children.Add(list);
            foreach (var theme in Themes)
            {
                var radio = new RadioButton
                {
                    Content = ThemeLabel(theme), Tag = theme, GroupName = "ThemeGroup",
                    Style = (Style)_window.FindResource("ThemeRadio"),
                    IsChecked = ThemeManager.Current == theme
                };
                radio.Checked += (_, _) => Theme_Checked(panel, theme);
                list.Children.Add(radio);
            }
            if (HasAccents(ThemeManager.Current))
            {
                _accentHost = BuildAccentStrip(ThemeManager.Current);
                panel.Children.Add(_accentHost);
            }

            // Film Grain: one switch for the grain texture on every surface (Services/FilmGrain.cs),
            // on its own row under both columns so the accent strip still runs the height of the
            // theme list only. Alt+Shift+G runs the same toggle, so the tooltip carries the chord.
            var grainRow = new StackPanel();
            Grid.SetRow(grainRow, 1);
            Grid.SetColumnSpan(grainRow, 2);
            var grainDivider = new Border
            {
                Height = 1,
                Margin = new Thickness(0, 0, 9, 8),
                Opacity = 0.18,
                IsHitTestVisible = false,
                OpacityMask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop(Colors.Transparent, 0),
                        new GradientStop(Colors.Black, 0.15),
                        new GradientStop(Colors.Black, 0.85),
                        new GradientStop(Colors.Transparent, 1),
                    },
                },
            };
            grainDivider.SetResourceReference(Border.BackgroundProperty, "TextBrush");
            grainRow.Children.Add(grainDivider);
            var filmGrain = new CheckBox
            {
                Style = (Style)_window.FindResource("ThemeCheck"),
                IsChecked = FilmGrain.Enabled,
                Margin = new Thickness(0),
            };
            filmGrain.SetResourceReference(ContentControl.ContentProperty, "Str_Theme_FilmGrain");
            filmGrain.Click += (_, _) => FilmGrain.Set(filmGrain.IsChecked == true);
            filmGrain.ToolTip = (_window.TryFindResource("Str_Theme_FilmGrain") as string) + " (Alt+Shift+G)";
            _filmGrainCheck = filmGrain;
            grainRow.Children.Add(filmGrain);
            panel.Children.Add(grainRow);

            _menu.Items.Add(panel);
        }

        /// <summary>Alt+Shift+G: Film Grain on or off, kept in step with the flyout's check box.</summary>
        internal void ToggleFilmGrain()
        {
            FilmGrain.Set(!FilmGrain.Enabled);
            _filmGrainCheck?.IsChecked = FilmGrain.Enabled;
        }

        private void Theme_Checked(Grid panel, Theme theme)
        {
            if (theme == ThemeManager.Current) return;
            _applying = true;
            try
            {
                ThemeTransition.CrossFade((FrameworkElement)_window.Content, () =>
                {
                    ThemeManager.Apply(theme);
                    DwmChrome.SetThemeBorder(_window);
                });
            }
            finally { _applying = false; }
            ReplaceAccentStrip(panel, theme, animate: true);
        }

        /// <summary>Swaps the strip for the newly selected family, sliding it in from zero width,
        /// or removes it for a fixed-palette theme.</summary>
        private void ReplaceAccentStrip(Grid panel, Theme family, bool animate)
        {
            if (_accentHost != null) panel.Children.Remove(_accentHost);
            _accentHost = null;
            _accentDots.Clear();
            if (!HasAccents(family)) return;

            _accentHost = BuildAccentStrip(family);
            panel.Children.Add(_accentHost);
            if (!animate) return;

            _accentHost.Width = 0;
            _accentHost.ClipToBounds = true;
            var slide = new DoubleAnimation(0, AccentStripWidth, TimeSpan.FromMilliseconds(AccentSlideMs))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            slide.Completed += (_, _) =>
            {
                if (_accentHost == null) return;
                _accentHost.BeginAnimation(FrameworkElement.WidthProperty, null);
                _accentHost.Width = AccentStripWidth;
            };
            _accentHost.BeginAnimation(FrameworkElement.WidthProperty, slide);
        }

        /// <summary>KillerPDF's picker: one vertical strip beside the complete theme list,
        /// repainted for whichever accent-capable family is selected.</summary>
        private Grid BuildAccentStrip(Theme family)
        {
            var host = new Grid { Width = AccentStripWidth };
            Grid.SetColumn(host, 1);

            var divider = new Border
            {
                Width = 1,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 6),
            };
            divider.SetResourceReference(Border.BackgroundProperty, "MenuBorderBrush");
            host.Children.Add(divider);

            var strip = new Grid { Margin = new Thickness(7, 6, 2, 6) };
            var colors = StripColors(family);
            for (int i = 0; i < colors.Length; i++)
            {
                strip.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                var (accent, hex) = colors[i];
                var dot = new Border
                {
                    Tag = accent,
                    Style = (Style)_window.FindResource("AccentDot"),
                    Width = 26,
                    Height = double.NaN,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, i == colors.Length - 1 ? 0 : 8),
                    Background = AccentStripBrush(family, accent, hex),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(),
                };
                dot.MouseEnter += (_, _) => RingAccentStrip(family);
                dot.MouseLeave += (_, _) => RingAccentStrip(family);
                if (ThemeManager.AccentChoiceFor(family) == accent)
                    dot.BorderBrush = _window.TryFindResource("TextBrush") as Brush;
                dot.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    if (ThemeManager.AccentChoiceFor(family) == accent) return;
                    _applying = true;
                    try
                    {
                        ThemeTransition.CrossFade((FrameworkElement)_window.Content, () =>
                        {
                            ThemeManager.ApplyAccent(family, accent);
                            DwmChrome.SetThemeBorder(_window);
                        });
                    }
                    finally { _applying = false; }
                    RingAccentStrip(family);
                };
                _accentDots.Add(dot);
                Grid.SetRow(dot, i);
                strip.Children.Add(dot);
            }
            host.Children.Add(strip);
            return host;
        }

        private static bool HasAccents(Theme theme) =>
            theme == Theme.Dark || theme == Theme.Light || theme == Theme.Black || theme == Theme.SE98;

        private object ThemeLabel(Theme theme) =>
            _window.TryFindResource(theme == Theme.SE98 ? "Str_Theme_98SE" : "Str_Theme_" + theme)
                ?? (theme == Theme.SE98 ? "98SE" : theme.ToString());

        // The ring marks the chosen accent and follows the mouse, and the pill under the mouse
        // lifts a little (KillerNotes). 98SE swatches stay flat.
        private void RingAccentStrip(Theme family)
        {
            var selected = ThemeManager.AccentChoiceFor(family);
            Brush? ring = _window.TryFindResource("TextBrush") as Brush;
            foreach (Border dot in _accentDots)
            {
                dot.BorderBrush = dot.IsMouseOver || (dot.Tag is Accent accent && accent == selected) ? ring : Brushes.Transparent;
                bool pop = dot.IsMouseOver && family != Theme.SE98;
                if (dot.RenderTransform is ScaleTransform scale)
                {
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(pop ? 1.06 : 1, TimeSpan.FromMilliseconds(100)));
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(pop ? 1.03 : 1, TimeSpan.FromMilliseconds(100)));
                }
                dot.Effect = pop
                    ? new System.Windows.Media.Effects.DropShadowEffect
                    { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 }
                    : null;
            }
        }

        // Each pill shows its accent the way a selection does: the palette's own SelectionBg
        // gradient, read from that accent's theme file. 98SE keeps its flat Win98 swatches.
        private static readonly Dictionary<(Theme, Accent), Brush> AccentStripBrushes = [];

        private static Brush AccentStripBrush(Theme family, Accent accent, string flatHex)
        {
            if (AccentStripBrushes.TryGetValue((family, accent), out var cached)) return cached;
            Brush brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(flatHex));
            if (family != Theme.SE98)
            {
                string path = accent == Accent.Green
                    ? $"Themes/{family}.xaml"
                    : $"Themes/Accents/{family}/{accent}.xaml";
                try
                {
                    var palette = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{path}") };
                    if (palette["SelectionBg"] is Brush selection) brush = selection.CloneCurrentValue();
                }
                catch { /* accent file not present: the flat swatch color stands */ }
            }
            brush.Freeze();
            AccentStripBrushes[(family, accent)] = brush;
            return brush;
        }

        // The swatch order and flat colors are the family's, top to bottom, shared with every app.
        private static (Accent Accent, string Hex)[] StripColors(Theme family) => family switch
        {
            Theme.Light =>
            [
                (Accent.Red, "#931A1A"), (Accent.Orange, "#C7710F"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#1B5E20"),
                (Accent.Teal, "#0D827E"), (Accent.Blue, "#18608E"), (Accent.Purple, "#5A1690"), (Accent.Magenta, "#A60070")
            ],
            Theme.Black =>
            [
                (Accent.Red, "#FF2929"), (Accent.Orange, "#FF910A"), (Accent.Yellow, "#FFEB00"), (Accent.Green, "#00FF66"),
                (Accent.Teal, "#0AFFE7"), (Accent.Blue, "#298DFF"), (Accent.Purple, "#B829FF"), (Accent.Magenta, "#FF2BBD")
            ],
            Theme.SE98 =>
            [
                (Accent.Red, "#800000"), (Accent.Orange, "#A05000"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#006000"),
                (Accent.Teal, "#008080"), (Accent.Blue, "#000080"), (Accent.Purple, "#5A376E"), (Accent.Magenta, "#750052")
            ],
            _ =>
            [
                (Accent.Red, "#DD504B"), (Accent.Orange, "#E8962C"), (Accent.Yellow, "#EAD900"), (Accent.Green, "#1EA54C"),
                (Accent.Teal, "#1FB8A8"), (Accent.Blue, "#4580D9"), (Accent.Purple, "#B982E3"), (Accent.Magenta, "#FF52C9")
            ],
        };
    }
}
