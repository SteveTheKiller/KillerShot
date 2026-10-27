using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerShot.Capture;
using CaptureMode = KillerShot.Capture.CaptureMode;
using Shortcut = KillerShot.Services.Shortcut;
using KillerShot.Controls;
using KillerShot.Editor;
using KillerShot.Services;

// The editor half of MainWindow: the document, the toolbar actions, the tools, color and width,
// zoom, recent captures and the footer readout.
namespace KillerShot.Shell
{
    public partial class MainWindow
    {
        private static readonly (string Key, Color Color)[] Palette =
        [
            ("Str_Color_Red",    Color.FromRgb(0xE5, 0x39, 0x35)),
            ("Str_Color_Orange", Color.FromRgb(0xFB, 0x8C, 0x00)),
            ("Str_Color_Yellow", Color.FromRgb(0xFD, 0xD8, 0x35)),
            ("Str_Color_Green",  Color.FromRgb(0x43, 0xA0, 0x47)),
            ("Str_Color_Teal",   Color.FromRgb(0x2E, 0xC4, 0xB6)),
            ("Str_Color_Blue",   Color.FromRgb(0x1E, 0x88, 0xE5)),
            ("Str_Color_Purple", Color.FromRgb(0x8E, 0x24, 0xAA)),
            ("Str_Color_Black",  Color.FromRgb(0x00, 0x00, 0x00)),
            ("Str_Color_White",  Color.FromRgb(0xFF, 0xFF, 0xFF)),
        ];

        private static readonly double[] StrokeWidths = [2, 4, 6, 10, 16];
        private static readonly double[] TextSizes = [14, 18, 24, 32, 48, 72];
        private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.5, 2, 3, 4, 6, 8, 12, 16];

        private EditorDocument? _doc;
        private bool _fitMode = true;
        private DispatcherTimer? _pixelTimer;

        private void InitEditor()
        {
            InitEditorContextMenu();
            if (int.TryParse(Settings.Get("ColorIndex"), out int ci) && ci >= 0 && ci < Palette.Length) Editor.Color = Palette[ci].Color;
            if (Settings.Get("AnnotationColor") is { } savedColor)
            {
                try { Editor.Color = (Color)ColorConverter.ConvertFromString(savedColor); }
                catch (FormatException) { }
            }
            if (double.TryParse(Settings.Get("StrokeWidth"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sw)) Editor.StrokeWidth = sw;
            if (double.TryParse(Settings.Get("TextSize"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ts)) Editor.TextSize = ts;
            if (Enum.TryParse<ToolKind>(Settings.Get("Tool"), out var tool) && tool != ToolKind.Crop) Editor.SetTool(tool);
            Editor.Filled = Settings.GetBool("FillShapes", false);

            Editor.ViewChanged += RefreshFooter;
            Editor.Status += text => StatusText.Text = text;
            App.Instance.DocumentChanged += OnAppDocumentChanged;
            Closed += (_, _) => App.Instance.DocumentChanged -= OnAppDocumentChanged;
            Closed += (_, _) =>
            {
                _recognitionClosed = true;
                SyncRecognitionState();
                if (_doc != null) _doc.Changed -= RefreshDocumentUi;
            };

            // The pixel readout follows the mouse without a handler on every move.
            _pixelTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            _pixelTimer.Tick += (_, _) => RefreshFooter();
            _pixelTimer.Start();
            Closed += (_, _) => _pixelTimer.Stop();

            BuildCaptureMenu();
            if (App.Instance.Video is { } video)
            {
                void OnVideoState() => Dispatcher.BeginInvoke(RefreshRecordButton);
                video.StateChanged += OnVideoState;
                Closed += (_, _) => video.StateChanged -= OnVideoState;
            }
            BindDocument(App.Instance.Document);
            HighlightActiveTool();
            BuildCaptureMenu();
        }

        private void OnAppDocumentChanged()
        {
            BindDocument(App.Instance.Document);
            _fitMode = true;
            ZoomToFit();
        }

        private void BindDocument(EditorDocument? doc)
        {
            if (_doc != null) _doc.Changed -= RefreshDocumentUi;
            // A new image comes to the front; the library steps back.
            bool changed = !ReferenceEquals(doc, _doc);
            if (changed) _libraryShown = false;
            _doc = doc;
            Editor.Document = doc;
            if (_doc != null) _doc.Changed += RefreshDocumentUi;
            RefreshDocumentUi();
            if (changed && doc?.Recovery == AlbumRecovery.Flattened) StatusText.Text = Loc("Str_Marks_Recovery");
            if (changed) _strip?.Follow(App.Instance.CurrentItem, doc?.FilePath);
        }

        private void DetachDocument()
        {
            Editor.CommitText();
            if (_doc != null) _doc.Changed -= RefreshDocumentUi;
            _doc = null;
            Editor.Document = null;
            SyncRecognitionState();
        }

        /// <summary>Title, enabled states, empty state and footer after any document change.</summary>
        private void RefreshDocumentUi()
        {
            SyncRecognitionState();
            SyncReview();
            SyncLibraryVisibility();
            HighlightActiveTool();

            string name = _doc?.FilePath is string p ? System.IO.Path.GetFileName(p) : "";
            RefreshDocumentQr();
            TitleFileLabel.Text = PlainTitleFileLabel.Text = name + (_doc?.Dirty == true && name.Length > 0 ? " *" : "");
            TitleFileLabel.Visibility = PlainTitleFileLabel.Visibility = name.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Title = name.Length > 0 ? name + " - KillerShot" : "KillerShot";
            RefreshFooter();
        }

        private void RefreshFooter()
        {
            if (_doc == null) { ImageInfoText.Text = ""; return; }
            // The zoom shows on the footer's zoom button (FooterView.cs).
            string zoom = Math.Round(Editor.Zoom * 100).ToString(CultureInfo.CurrentCulture) + "%";
            if (!Equals(ZoomLevelBtn.Content, zoom)) ZoomLevelBtn.Content = zoom;
            string text = $"{_doc.PixelWidth} x {_doc.PixelHeight}";
            if (Editor.PixelUnderMouse() is Point p) text += $"   {p.X:0}, {p.Y:0}";
            if (ImageInfoText.Text != text) ImageInfoText.Text = text;
        }

        // ---- tools ----

        private Button ToolButton(ToolKind t) => t switch
        {
            ToolKind.Select => ToolSelectBtn,
            ToolKind.Pen => ToolPenBtn,
            ToolKind.Highlighter => ToolHighlighterBtn,
            ToolKind.Arrow => ToolArrowBtn,
            ToolKind.Line => ToolLineBtn,
            ToolKind.Rectangle => ToolRectangleBtn,
            ToolKind.Ellipse => ToolEllipseBtn,
            ToolKind.Text => ToolTextBtn,
            ToolKind.Step => ToolStepBtn,
            ToolKind.Callout => ToolCalloutBtn,
            ToolKind.Redact => ToolRedactBtn,
            _ => ToolCropBtn,
        };

        private void SelectTool(ToolKind tool)
        {
            if (_doc == null) return;
            Editor.SetTool(tool);
            if (tool != ToolKind.Crop) Settings.Set("Tool", tool.ToString());
            HighlightActiveTool();
            StatusText.Text = tool == ToolKind.Crop ? Loc("Str_Status_CropHint") : Loc(ToolLabelKey(tool));
            Editor.Focus();
        }

        private static string ToolLabelKey(ToolKind t) => "Str_Tool_" + t;

        /// <summary>The active tool's button carries the accent, the family's "this one is on" mark.</summary>
        private void HighlightActiveTool()
        {
            foreach (ToolKind t in Enum.GetValues<ToolKind>())
            {
                var b = ToolButton(t);
                if (t == Editor.Tool && _doc != null) b.SetResourceReference(ForegroundProperty, "SelectionAccent");
                else b.ClearValue(ForegroundProperty);
            }
            // The width button shows the text size while the text tool is active.
            RefreshWidthButton();
            RefreshFillButton();
            // The active tool stays on the bar however narrow the window is.
            QueueToolbarReflow();
        }

        /// <summary>The Record button reads Stop, in the accent, while a recording runs.</summary>
        private void RefreshRecordButton()
        {
            bool on = App.Instance.Video?.IsRecording == true;
            RecordBtn.Content = BuildToolbarContent(_iconSize, _labelMode, on ? 0xE71A : 0xE7C8, Loc(on ? "Str_Rec_Stop" : "Str_Btn_Record"));
            if (on) RecordBtn.SetResourceReference(ForegroundProperty, "SelectionAccent"); else RecordBtn.ClearValue(ForegroundProperty);
            QueueToolbarReflow();
        }

        private void RefreshFillButton()
        {
            FillBtn.Content = BuildToolbarContent(_iconSize, _labelMode, 0, Loc("Str_Btn_Fill"), DrawnIcon(FillBtn));
            if (Editor.Filled && _doc != null) FillBtn.SetResourceReference(ForegroundProperty, "SelectionAccent"); else FillBtn.ClearValue(ForegroundProperty);
        }

        /// <summary>F: new rectangles and ellipses are filled, or outlines again.</summary>
        private void ToggleFill()
        {
            if (_doc == null) return;
            Editor.Filled = !Editor.Filled;
            Settings.SetBool("FillShapes", Editor.Filled);
            RefreshFillButton();
            StatusText.Text = Loc(Editor.Filled ? "Str_Status_FillOn" : "Str_Status_FillOff");
        }

        private void FillBtn_Click(object sender, RoutedEventArgs e) => ToggleFill();

        private void RefreshWidthButton() =>
            WidthBtn.Content = BuildToolbarContent(_iconSize, _labelMode, 0, Loc("Str_Btn_Width"), DrawnIcon(WidthBtn));

        private void Tool_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ToolKind>(tag, out var t)) SelectTool(t);
        }

        /// <summary>Single-key shortcut shown in a tool's tooltip, and the Ctrl shortcuts of the
        /// file and edit buttons. One place, so a tooltip cannot disagree with the key handler.</summary>
        private string? ShortcutFor(Button b)
        {
            if (b == CaptureBtn) return "Ctrl+N";
            if (b == RecordBtn) return "Ctrl+R";
            if (b == OpenBtn) return "Ctrl+O";
            if (b == SaveBtn) return "Ctrl+S";
            if (b == SaveAsBtn) return "Ctrl+Shift+S";
            if (b == CopyBtn) return "Ctrl+C";
            if (b == CopyTextBtn) return "Ctrl+Shift+C";
            if (b == PrintBtn) return "Ctrl+P";
            if (b == UndoBtn) return "Ctrl+Z";
            if (b == RedoBtn) return "Ctrl+Y";
            if (b == FitBtn) return "Ctrl+0";
            if (b == ActualSizeBtn) return "Ctrl+1";
            if (b == ToolSelectBtn) return "V";
            if (b == ToolPenBtn) return "P";
            if (b == ToolHighlighterBtn) return "H";
            if (b == ToolArrowBtn) return "A";
            if (b == ToolLineBtn) return "L";
            if (b == FillBtn) return "F";
            if (b == ToolStepBtn) return "N";
            if (b == ToolCalloutBtn) return "B";
            if (b == ToolRectangleBtn) return "R";
            if (b == ToolEllipseBtn) return "E";
            if (b == ToolTextBtn) return "T";
            if (b == ToolRedactBtn) return "X";
            if (b == ToolCropBtn) return "C";
            return null;
        }

        /// <summary>Icons that are drawn rather than taken from the glyph font: the current color as
        /// a swatch and the current stroke width as a bar.</summary>
        private Func<double, FrameworkElement>? DrawnIcon(Button b)
        {
            if (b == ColorBtn)
                return size =>
                {
                    var swatch = new Border
                    {
                        Width = size, Height = size,
                        Background = new SolidColorBrush(Editor.Color),
                        BorderBrush = (Brush)FindResource("CardBorderBrush"),
                        BorderThickness = new Thickness(1),
                    };
                    swatch.SetResourceReference(Border.CornerRadiusProperty, "SmallCornerRadius");
                    return swatch;
                };
            if (b == FillBtn)
                return size =>
                {
                    // A square: solid in the mark color when fill is on, an outline when off.
                    var square = new Border { Width = size * 0.72, Height = size * 0.72, BorderThickness = new Thickness(Math.Max(1.5, size / 12)) };
                    square.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Foreground") { Source = FillBtn });
                    if (Editor.Filled) square.Background = new SolidColorBrush(Editor.Color);
                    return square;
                };
            if (b == ToolStepBtn)
                return size =>
                {
                    var badge = new Border { Width = size * 0.86, Height = size * 0.86, CornerRadius = new CornerRadius(size) };
                    badge.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Foreground") { Source = ToolStepBtn });
                    var number = new TextBlock { Text = "1", FontSize = size * 0.58, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    number.SetResourceReference(TextBlock.ForegroundProperty, "BackgroundBrush");
                    badge.Child = number;
                    return badge;
                };
            if (b == WidthBtn)
                return size =>
                {
                    if (Editor.Tool == ToolKind.Text)
                    {
                        var t = new TextBlock { Text = Editor.TextSize.ToString("0"), FontSize = size * 0.7, FontWeight = FontWeights.SemiBold };
                        return t;
                    }
                    var bar = new Rectangle
                    {
                        Width = size, Height = Math.Max(1.5, Math.Min(size * 0.6, Editor.StrokeWidth * size / 20)),
                    };
                    bar.SetResourceReference(Rectangle.RadiusXProperty, "ToolbarWidthBarRadius");
                    bar.SetResourceReference(Rectangle.RadiusYProperty, "ToolbarWidthBarRadius");
                    bar.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("Foreground") { Source = WidthBtn });
                    return bar;
                };
            return null;
        }

        // ---- color and width ----

        private void ColorBtn_Click(object sender, RoutedEventArgs e)
        {
            var original = Editor.Color;
            var picker = new ColorPickerDialog(this, original);
            picker.ColorChanged += color => { Editor.Color = color; ApplyToolbarStyle(); };
            picker.ShowDialog();
            Editor.Color = picker.Accepted ? picker.SelectedColor : original;
            ApplyToolbarStyle();
            if (!picker.Accepted) return;
            Settings.Set("AnnotationColor", Editor.Color.ToString());
            RecolorSelection(Editor.Color);
        }

        /// <summary>Picking a color with a mark selected recolors that mark, as one undo step.</summary>
        private void RecolorSelection(Color color)
        {
            if (_doc == null || Editor.Selected is not { } a || a is ShapeAnnotation { Kind: ShapeKind.Redact }) return;
            Annotation recolored = a switch
            {
                StrokeAnnotation s => s with { Color = color },
                ShapeAnnotation s => s with { Color = color },
                TextAnnotation t => t with { Color = color },
                _ => a,
            };
            _doc.Replace(a, recolored);
        }

        private void WidthBtn_Click(object sender, RoutedEventArgs e)
        {
            WidthMenu.Items.Clear();
            bool text = Editor.Tool == ToolKind.Text;
            WidthMenu.Items.Add(new MenuItem { Header = Loc(text ? "Str_Menu_TextSize" : "Str_Menu_StrokeWidth"), IsEnabled = false });
            WidthMenu.Items.Add(new Separator());
            foreach (var v in text ? TextSizes : StrokeWidths)
            {
                double value = v;
                var mi = new MenuItem
                {
                    Header = string.Format(Loc("Str_Menu_Pixels"), value),
                    IsCheckable = true,
                    IsChecked = text ? Math.Abs(Editor.TextSize - value) < 0.01 : Math.Abs(Editor.StrokeWidth - value) < 0.01,
                };
                mi.Click += (_, _) =>
                {
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (text) { Editor.TextSize = value; Settings.Set("TextSize", value.ToString(inv)); }
                    else { Editor.StrokeWidth = value; Settings.Set("StrokeWidth", value.ToString(inv)); }
                    ApplyToolbarStyle();
                };
                WidthMenu.Items.Add(mi);
            }
            OpenFlyout(WidthMenu, WidthBtn);
        }

        private void OpenFlyout(ContextMenu menu, Button anchor)
        {
            // A button in the More menu has no place on the bar, so its flyout opens under the
            // More chevron instead.
            menu.PlacementTarget = anchor.IsVisible ? anchor : _toolbarHidden ? ToolbarOverflowButton : MoreBtn;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            Anim.FadeIn(menu);
        }

        // ---- capture ----

        private void BuildCaptureMenu()
        {
            CaptureMenu.Items.Clear();
            foreach (var (mode, key, action) in new (CaptureMode, string, ShortcutAction?)[]
            {
                (CaptureMode.Region, "Str_Mode_Region", null),
                (CaptureMode.Window, "Str_Mode_Window", ShortcutAction.Window),
                (CaptureMode.Monitor, "Str_Mode_Monitor", ShortcutAction.Monitor),
                (CaptureMode.AllMonitors, "Str_Mode_AllMonitors", ShortcutAction.AllMonitors),
                (CaptureMode.Qr, "Str_Mode_Qr", null),
            })
            {
                var m = mode;
                string gesture = action is { } a && Shortcut.TryParse(CaptureSettings.ShortcutText(a), out var sc) ? sc.Short : "";
                if (mode == CaptureMode.Region) gesture = "Ctrl+N";
                var mi = new MenuItem { Header = Loc(key), InputGestureText = gesture,
                    IsCheckable = true, IsChecked = mode == CaptureMode.Region };
                mi.Click += (_, _) => StartCapture(m);
                CaptureMenu.Items.Add(mi);
            }
            if (App.Instance.Capture is { } capture)
            {
                CaptureMenu.Items.Add(new Separator());
                foreach (var item in capture.RegionMenuItems(a => Shortcut.TryParse(CaptureSettings.ShortcutText(a), out var s) ? s.Short : ""))
                    CaptureMenu.Items.Add(item);
            }
        }

        private void CaptureBtn_Click(object sender, RoutedEventArgs e)
        {
            BuildCaptureMenu();
            FlyoutPlacement.AttachInsidePane(CaptureMenu, CaptureBtn.IsVisible ? CaptureBtn
                : _toolbarHidden ? ToolbarOverflowButton : MoreBtn);
            CaptureMenu.IsOpen = true;
            Anim.FadeIn(CaptureMenu);
        }

        // ---- library ----

        /// <summary>True while the library covers an open image (Ctrl+L). With no image open the
        /// library always shows.</summary>
        private bool _libraryShown;

        [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_doc))]
        private bool EditorWorkspaceActive => _doc != null && !_libraryShown;

        private void SyncEditorCommands()
        {
            foreach (var b in new[] { SaveBtn, SaveAsBtn, CopyBtn, CopyTextBtn, PrintBtn, ToolSelectBtn, ToolPenBtn, ToolHighlighterBtn, ToolArrowBtn, ToolLineBtn,
                                      ToolRectangleBtn, ToolEllipseBtn, FillBtn, ToolTextBtn, ToolStepBtn, ToolCalloutBtn, ToolRedactBtn, ToolCropBtn, ImageBtn,
                                      ColorBtn, WidthBtn, FitBtn, ActualSizeBtn })
                b.IsEnabled = EditorWorkspaceActive;
            UndoBtn.IsEnabled = EditorWorkspaceActive && _doc.CanUndo;
            RedoBtn.IsEnabled = EditorWorkspaceActive && _doc.CanRedo;
        }

        private void InitLibrary()
        {
            Library.SearchAnchor = LibrarySearchButton;
            // The library's file name, without its extension, as Killendar and KillerNotes name
            // their open database; the tooltip has the full path.
            TitleLibraryLabel.Text = System.IO.Path.GetFileNameWithoutExtension(CaptureLibrary.FilePath);
            TitleLibraryLabel.ToolTip = CaptureLibrary.FilePath;
            Library.OpenRequested += item =>
            {
                try
                {
                    App.Instance.OpenLibraryCapture(item);
                }
                catch (Exception ex) { StatusText.Text = ex.Message; }
            };
            Library.CaptureRequested += () => StartCapture(CaptureMode.Region);
            Library.ViewChanged += SyncContentPane;
            Library.ViewChanged += SyncStripPane;
            Library.StatusMessage += text => StatusText.Text = text;
            Library.LinkRequested += (payload, anchor) => ShowPayloadCard(payload, anchor, Loc("Str_Ocr_Links"));
        }

        private void LibrarySearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_doc != null && !_libraryShown) ToggleLibrary();
            UpdateLayout();
            Library.FocusSearch();
        }

        private void NewCollectionButton_Click(object sender, RoutedEventArgs e) => Library.CreateCollection();


        /// <summary>The library's title-bar name opens its database settings.</summary>
        private void TitleLibraryLabel_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowLibrarySettings();
        }

        /// <summary>Ctrl+L: the library over the open image, and back. With no image open the
        /// library is already showing, so it only takes focus.</summary>
        private void ToggleLibrary()
        {
            Editor.CommitText();
            _libraryShown = _doc != null && !_libraryShown;
            SyncLibraryVisibility();
            if (Library.IsVisible) Library.Focus();
        }

        private void SyncLibraryVisibility()
        {
            bool library = _doc == null || _libraryShown;
            Library.Visibility = library ? Visibility.Visible : Visibility.Collapsed;
            SyncStripPlacement();
            EditorScroll.Visibility = library ? Visibility.Collapsed : Visibility.Visible;
            SyncEditorCommands();
            SyncContentPane();
            SyncFooterView();
        }

        private System.Windows.Media.Effects.Effect? _paneShadow;

        /// <summary>The editor and every album view keep the same lifted content pane.</summary>
        private void SyncContentPane()
            => ShowContentPane(true);

        /// <summary>Keep the themed content surface, border and grain together.</summary>
        private void ShowContentPane(bool on)
        {
            _paneShadow ??= ContentPane.Effect;
            if (on)
            {
                ContentPane.SetResourceReference(Border.BackgroundProperty, "ContentPaneBrush");
                ContentPane.SetResourceReference(Border.BorderThicknessProperty, "ContentPaneBaseBorderThickness");
                ContentPane.Effect = _paneShadow;
            }
            else
            {
                ContentPane.Background = Brushes.Transparent;
                ContentPane.BorderThickness = new Thickness(0);
                ContentPane.Effect = null;
            }
            // The grain, the outline and the bevel rings over the pane go with it.
            if (ContentPane.Child is Grid paneGrid)
                foreach (UIElement child in paneGrid.Children)
                    if (child == ContentPaneGrain || Panel.GetZIndex(child) >= 99)
                        child.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) ContentPaneGrain.SetResourceReference(VisibilityProperty, "FilmGrainVisibility");
        }

        /// <summary>Window mode from inside the editor captures the window that was in front before
        /// KillerShot, which is the one the user means.</summary>
        private void StartCapture(CaptureMode mode)
        {
            Editor.CommitText();
            _ = App.Instance.Capture?.Start(mode);
        }

        // ---- recent ----

        private void RecentBtn_Click(object sender, RoutedEventArgs e)
        {
            FillRecentMenu(RecentMenu);
            OpenFlyout(RecentMenu, _toolbarHidden ? ToolbarOverflowButton : OpenBtn.IsVisible ? OpenBtn : MoreBtn);
        }

        private void FillRecentMenu(ItemsControl menu)
        {
            menu.Items.Clear();
            var items = CaptureLibrary.Items().Take(12).ToList();
            if (!CaptureLibrary.IsOpen)
                menu.Items.Add(new MenuItem { Header = Loc("Str_Lib_LockedTitle"), IsEnabled = false });
            else if (items.Count == 0)
                menu.Items.Add(new MenuItem { Header = Loc("Str_Recent_None"), IsEnabled = false });
            foreach (var item in items)
            {
                var capture = item;
                var thumb = new Image { Width = 64, Height = 40, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 10, 2),
                                        Source = CaptureLibrary.LoadThumb(item.Id) };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = LibraryView.Title(item), MaxWidth = 280, TextTrimming = TextTrimming.CharacterEllipsis });
                var when = new TextBlock { Text = item.Taken.ToString("g"), FontSize = 11 };
                when.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                text.Children.Add(when);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(KillerShot.Controls.QrMark.Wrap(thumb, item.HasQr));
                row.Children.Add(text);
                var mi = new MenuItem { Header = row };
                System.Windows.Automation.AutomationProperties.SetName(mi, LibraryView.Describe(item));
                mi.Click += (_, _) =>
                {
                    try
                    {
                        App.Instance.OpenLibraryCapture(capture);
                    }
                    catch (Exception ex) { StatusText.Text = ex.Message; }
                };
                menu.Items.Add(mi);
            }
            menu.Items.Add(new Separator());
            var library = new MenuItem { Header = Loc("Str_Recent_ShowLibrary"), InputGestureText = "Ctrl+L" };
            library.Click += (_, _) => { if (_doc != null) { _libraryShown = true; SyncLibraryVisibility(); } };
            menu.Items.Add(library);
        }

        // ---- file ----

        private void OpenBtn_Click(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var open = new MenuItem { Header = Loc("Str_Btn_Open"), InputGestureText = "Ctrl+O", Icon = ToolbarUnitIcon("open") };
            open.Click += (_, _) => OpenFile();
            menu.Items.Add(open);
            var paste = new MenuItem { Header = Loc("Str_Img_PasteNew"), InputGestureText = "Ctrl+Shift+V" };
            paste.Click += (_, _) => PasteAsNewPicture();
            menu.Items.Add(paste);
            var recent = new MenuItem { Header = Loc("Str_Btn_Recent"), Icon = ToolbarUnitIcon("recent") };
            FillRecentMenu(recent);
            menu.Items.Add(recent);
            OpenFlyout(menu, _toolbarHidden ? ToolbarOverflowButton : OpenBtn.IsVisible ? OpenBtn : MoreBtn);
        }

        private void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!EditorWorkspaceActive) return;
            var menu = new ContextMenu();
            foreach (var (id, key, shortcut, action) in new (string, string, string, Action)[]
                { ("save", "Str_Btn_Save", "Ctrl+S", Save), ("saveas", "Str_Btn_SaveAs", "Ctrl+Shift+S", SaveAs) })
            {
                var item = new MenuItem { Header = Loc(key), InputGestureText = shortcut, Icon = ToolbarUnitIcon(id), IsEnabled = _doc != null };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
            }
            OpenFlyout(menu, _toolbarHidden ? ToolbarOverflowButton : SaveBtn.IsVisible ? SaveBtn : MoreBtn);
        }
        private void SaveAsBtn_Click(object sender, RoutedEventArgs e) => SaveAs();
        private void CopyBtn_Click(object sender, RoutedEventArgs e) => CopyResult();
        private void PrintBtn_Click(object sender, RoutedEventArgs e) => PrintResult();
        internal void PrintOpenPicture() => PrintResult();

        private void PrintResult()
        {
            if (!EditorWorkspaceActive) return;
            Editor.CommitText();
            try
            {
                if (PrintOutput.Print(_doc.Render(), _doc.FilePath != null ? System.IO.Path.GetFileName(_doc.FilePath) : "KillerShot"))
                    StatusText.Text = Loc("Str_Status_Printed");
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(Loc("Str_Status_PrintFailed"), ex.Message);
            }
        }
        private void UndoBtn_Click(object sender, RoutedEventArgs e) => Undo();
        private void RedoBtn_Click(object sender, RoutedEventArgs e) => Redo();
        private void FitBtn_Click(object sender, RoutedEventArgs e) => ZoomToFit();
        private void ActualSizeBtn_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);

        private void OpenFile()
        {
            var dlg = new FileDialog(FileDialogMode.Open)
            {
                Title = Loc("Str_Dlg_OpenTitle"),
                Filter = Loc("Str_Dlg_OpenFilter"),
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
        }

        internal void CommitPendingText() => Editor.CommitText();
        internal void SaveDocumentForExit()
        {
            _libraryShown = false;
            SyncLibraryVisibility();
            Save();
        }

        private bool OpenPath(string path)
        {
            try
            {
                var bmp = ImageOutput.Load(path);
                if (!App.Instance.SetDocument(new EditorDocument(bmp, path, System.IO.File.GetLastWriteTime(path)))) return false;
                StatusText.Text = Loc("Str_Status_Ready");
                return true;
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(Loc("Str_Status_OpenFailed"), ex.Message);
                return false;
            }
        }

        private void Save()
        {
            if (!EditorWorkspaceActive) return;
            if (_doc.FilePath == null || !ImageOutput.CanSaveInPlace(_doc.FilePath)) { SaveAs(); return; }
            WriteTo(_doc, _doc.FilePath);
        }

        private void SaveAs()
        {
            if (!EditorWorkspaceActive) return;
            Editor.CommitText();
            var document = _doc;
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Title = Loc("Str_Dlg_SaveTitle"),
                Filter = Loc("Str_Dlg_ImageFilter"),
                DefaultExt = ".png",
                FileName = _doc.FilePath != null ? ImageOutput.CanSaveInPlace(_doc.FilePath)
                    ? System.IO.Path.GetFileName(_doc.FilePath)
                    : System.IO.Path.GetFileNameWithoutExtension(_doc.FilePath) + "-static.png"
                    : $"KillerShot {_doc.Taken:yyyy-MM-dd HHmmss}.png",
                InitialDirectory = _doc.FilePath != null ? System.IO.Path.GetDirectoryName(_doc.FilePath) ?? "" : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dlg.ShowDialog(this) != true) return;
            if (!ReferenceEquals(document, _doc))
            {
                StatusText.Text = string.Format(Loc("Str_Status_SaveFailed"), Loc("Str_Save_DocumentChanged"));
                return;
            }
            WriteTo(document, dlg.FileName);
        }

        private void WriteTo(EditorDocument document, string path)
        {
            if (!ReferenceEquals(document, _doc)) return;
            Editor.CommitText();
            try
            {
                if (document.FilePath is string source && System.IO.File.Exists(source)
                    && string.Equals(System.IO.Path.GetFullPath(source), System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                    && !ImageOutput.CanSaveInPlace(source))
                    throw new NotSupportedException(Loc("Str_Save_StaticOriginal"));
                ImageOutput.Save(document.Render(), path, CaptureSettings.JpegQuality);
                document.FilePath = path;
                document.MarkSaved();
                App.Instance.DocumentSavedToFile(document);
                RefreshDocumentUi();
                StatusText.Text = string.Format(Loc("Str_Status_Saved"), System.IO.Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                StatusText.Text = string.Format(Loc("Str_Status_SaveFailed"), ex.Message);
            }
        }

        private void CopyResult()
        {
            if (!EditorWorkspaceActive) return;
            Editor.CommitText();
            var bmp = _doc.Render();
            StatusText.Text = ImageOutput.TryCopy(bmp, out var error)
                ? string.Format(Loc("Str_Status_Copied"), bmp.PixelWidth, bmp.PixelHeight)
                : string.Format(Loc("Str_Tray_ClipboardBusy"), error);
        }

        private void Undo() { if (!EditorWorkspaceActive) return; Editor.CommitText(); _doc.Undo(); }
        private void Redo() { if (!EditorWorkspaceActive) return; Editor.CommitText(); _doc.Redo(); }

        // ---- zoom ----

        private void SetZoom(double z, Point? anchor = null)
        {
            if (_doc == null) return;
            _fitMode = false;
            // Keep the point under the mouse (or the view center) in place while zooming.
            var view = anchor ?? new Point(EditorScroll.ViewportWidth / 2, EditorScroll.ViewportHeight / 2);
            double old = Editor.Zoom;
            var onCanvas = EditorScroll.TranslatePoint(view, Editor);
            Editor.Zoom = z;
            EditorScroll.UpdateLayout();
            double k = Editor.Zoom / old;
            var target = Editor.TranslatePoint(new Point(onCanvas.X * k, onCanvas.Y * k), EditorScroll);
            EditorScroll.ScrollToHorizontalOffset(EditorScroll.HorizontalOffset + target.X - view.X);
            EditorScroll.ScrollToVerticalOffset(EditorScroll.VerticalOffset + target.Y - view.Y);
            RefreshFooter();
        }

        private void ZoomStep(int direction, Point? anchor = null)
        {
            double z = Editor.Zoom;
            double next = direction > 0
                ? ZoomSteps.FirstOrDefault(s => s > z + 0.001, ZoomSteps[^1])
                : ZoomSteps.LastOrDefault(s => s < z - 0.001, ZoomSteps[0]);
            SetZoom(next, anchor);
        }

        private void ZoomToFit()
        {
            if (_doc == null) return;
            _fitMode = true;
            // The frame keeps a 16 unit margin on every side.
            var vp = new Size(Math.Max(1, EditorScroll.ActualWidth - 34), Math.Max(1, EditorScroll.ActualHeight - 34));
            Editor.Zoom = Editor.FitZoom(vp);
            RefreshFooter();
        }

        private void EditorScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            e.Handled = true;
            ZoomStep(e.Delta > 0 ? 1 : -1, e.GetPosition(EditorScroll));
        }

        private void EditorScroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_fitMode) ZoomToFit();
        }
    }
}
