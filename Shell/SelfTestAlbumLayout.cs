using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using KillerFamily.Layout;
using KillerShot.Editor;
using KillerShot.Services;
using Path = System.IO.Path;

namespace KillerShot.Shell;

internal static class SelfTestAlbumLayout
{
    internal static bool TryRun(string[] args)
    {
        if (args.Length != 2 || args[0] != "--selftest-album-layout") return false;
        _ = RunAsync(Path.GetFullPath(args[1]));
        return true;
    }

    private static async Task RunAsync(string folder)
    {
        int code = 1;
        var report = new List<string>
        {
            "H1: The 92px timeline gutter places its centered marker too far from the rail; a compact gutter reduces that inset.",
            "H2: Normal content padding causes the gap; measuring the marker in library coordinates distinguishes that from the gutter.",
            "H3: Album mode hides the existing DockHost pane and chevron; keeping the pane available restores both.",
            "H4: Album collapse updates share editor preferences; switching modes must preserve the editor layout independently."
        };
        MainWindow? window = null;
        try
        {
            if (Directory.Exists(folder)) throw new InvalidOperationException("Use a new album layout test folder.");
            Directory.CreateDirectory(folder);
            Settings.UseFolder(Path.Combine(folder, "settings"));
            SqlCipherBootstrap.UseCacheFolderForTests(Path.Combine(folder, "native"));
            CaptureLibrary.UseFolder(Path.Combine(folder, "library"));
            CaptureLibrary.BackgroundWork = false;
            FolderCaptures.UseFolder(Path.Combine(folder, "Screenshots"));
            FolderCaptures.Enabled = false;
            App.InitializeKit();
            App.CloseToTray = true;
            Settings.Set("StripSide", "Right");
            Settings.SetBool("StripMinimized", false);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var image = BitmapSource.Create(168, 104, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 168 * 104 * 4).ToArray(), 168 * 4);
            image.Freeze();
            CaptureLibrary.AddNow(image, DateTime.Today, "region", "Layout sample", null);
            long collectionId = CaptureLibrary.CreateCollection("Layout collection", CaptureLibrary.Items().Select(item => item.Id));
            var workArea = SystemParameters.WorkArea;
            window = new MainWindow
            {
                Width = Math.Min(1000, workArea.Width - 40), Height = Math.Min(700, workArea.Height - 40),
                ShowActivated = false, Left = workArea.Left + 20, Top = workArea.Top + 20,
            };
            var root = (FrameworkElement)window.Content;
            window.Show();
            // The window's CenterScreen startup can choose another monitor and override the
            // initial Left/Top. Put this native fixture in the measured work area after Show.
            window.Left = workArea.Left + 20;
            window.Top = workArea.Top + 20;
            window.Width = Math.Min(1000, workArea.Width - 40);
            window.Height = Math.Min(700, workArea.Height - 40);
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Task.Delay(350);
            var library = (LibraryView)window.FindName("Library");
            Check(window.FindName("LibrarySearchButton") is Button searchButton
                && ReferenceEquals(searchButton.Parent, window.FindName("RailStack")),
                "Search captures needs its own button in the left menu.");
            Check(window.FindName("NewCollectionButton") is Button newCollectionButton
                && ReferenceEquals(newCollectionButton.Parent, window.FindName("RailStack")),
                "New collection needs its own button in the left menu.");
            Check(library.FindName("HeaderBar") == null, "The old search header still reserves a row above the album.");
            string? failure = null;
            foreach (var theme in new[] { Theme.Black, Theme.Dark, Theme.SE98 })
            {
                ThemeManager.Apply(theme);
                ThemeManager.ApplyAccent(theme, Accent.Red);
                library.SetView("timeline");
                Layout(root);
                await Task.Delay(100);
                Layout(root);
                var dot = Visuals(library).OfType<Ellipse>().First(element => element.Width == 12 && element.Height == 12);
                var center = dot.TransformToAncestor(library).Transform(new Point(6, 6));
                var tile = Visuals(library).OfType<Image>().First(element => element.Width == 168);
                report.Add($"{theme}: marker_x={center.X:0.##} tile_width={tile.ActualWidth:0.##} tile_height={tile.ActualHeight:0.##}");
                Render(root, Path.Combine(folder, "timeline-" + theme + ".png"));
                if (center.X < 30 || center.X > 34)
                    failure = $"The timeline marker is {center.X:0.##}px from the library edge instead of the compact 32px inset.";
                if (tile.ActualWidth != 168 || tile.ActualHeight != 104)
                    throw new InvalidOperationException("The timeline thumbnail geometry changed.");
            }
            if (failure != null) throw new InvalidOperationException(failure);
            report.Add("timeline_compact_inset_preserves_card_geometry=True");

            var dock = (DockHost)window.FindName("StripDock");
            var pane = (FrameworkElement)dock.Pane!;
            var chevron = Visuals(dock).OfType<Button>().Single(button => button.Content is string text && text is "\uE76B" or "\uE76C");
            foreach (string view in new[] { "shelves", "flow", "grid", "timeline" })
            {
                library.SetView(view);
                Layout(root);
                Check(dock.ShowPane && dock.Side == DockSide.Left && Visuals(dock).Contains(pane)
                    && chevron.Visibility == Visibility.Visible, "The album view hides its existing sidebar and chevron: " + view);
            }
            Render(root, Path.Combine(folder, "album-sidebar-expanded.png"));
            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Layout(root);
            Check(dock.IsMinimized && !Visuals(dock).Contains(pane) && chevron.Visibility == Visibility.Visible,
                "The album chevron must collapse the sidebar and remain available to restore it.");
            Check(Settings.Get("StripSide") == "Right" && !Settings.GetBool("StripMinimized", true),
                "Collapsing the album sidebar overwrote the editor layout preference.");
            Render(root, Path.Combine(folder, "album-sidebar-collapsed.png"));
            App.Instance.SetDocument(new EditorDocument(image));
            Layout(root);
            Check(dock.Side == DockSide.Right && !dock.IsMinimized, "Returning to the editor must restore its right, expanded strip.");
            dock.DockTo(DockSide.Bottom);
            dock.Minimize();
            App.Instance.SetDocument(null);
            Layout(root);
            Check(dock.Side == DockSide.Left && dock.IsMinimized, "Returning to the album must preserve its own collapsed sidebar.");
            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            App.Instance.SetDocument(new EditorDocument(image));
            Layout(root);
            Check(dock.Side == DockSide.Bottom && dock.IsMinimized, "Expanding the album sidebar must preserve the editor's bottom, collapsed strip.");
            report.Add("album_sidebar_chevron_available_in_all_views=True");
            report.Add("album_and_editor_minimized_states_are_independent=True");

            report.Add("Chooser H1: The populated collection picker is absent from the carousel's visual tree, so adding it restores selection.");
            report.Add("Chooser H2: The picker is present but clipped; measuring its visible bounds inside the strip distinguishes clipping.");
            report.Add("Chooser H3: Collection selection is stale; choosing a source must update the strip and its current caption.");
            dock.Restore();
            Layout(root);
            var picker = Visuals(pane).OfType<ComboBox>().SingleOrDefault();
            Check(picker != null && picker.IsVisible, "The horizontal carousel frame needs its collection chooser.");
            picker!.IsDropDownOpen = true;
            await Task.Delay(100);
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(item => item.Tag is string tag && tag == "c" + collectionId);
            picker.IsDropDownOpen = false;
            Layout(root);
            var strip = (CollectionStrip)pane;
            Check(strip.Source == "c" + collectionId && strip.SourceCaption == "Layout collection",
                "The carousel chooser did not switch to its selected collection.");
            var pickerBounds = picker.TransformToAncestor(pane).TransformBounds(new Rect(picker.RenderSize));
            Check(pickerBounds.Left >= 0 && pickerBounds.Top >= 0 && pickerBounds.Right <= pane.ActualWidth
                && pickerBounds.Bottom <= pane.ActualHeight, "The carousel chooser is clipped outside its frame.");
            Render(root, Path.Combine(folder, "carousel-collection-chooser.png"));
            report.Add("carousel_collection_chooser_switches_source_inside_frame=True");

            ((Button)window.FindName("LibrarySearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            var popup = (Popup)library.FindName("SearchPopup");
            Check(popup.IsOpen && ReferenceEquals(popup.PlacementTarget, window.FindName("LibrarySearchButton")),
                "The search button did not open its own search flyout.");
            var contentPane = (FrameworkElement)window.FindName("ContentPane");
            Point popupPoint = ((FrameworkElement)popup.Child).PointToScreen(new Point());
            Point panePoint = contentPane.PointToScreen(new Point());
            var dpi = VisualTreeHelper.GetDpi(contentPane);
            var anchor = (FrameworkElement)popup.PlacementTarget;
            var face = (FrameworkElement)popup.Child;
            var placement = popup.CustomPopupPlacementCallback(face.RenderSize, anchor.RenderSize, new Point())[0];
            report.Add($"search_debug pane={panePoint} anchor={anchor.PointToScreen(new Point())} face={popupPoint} pane_size={contentPane.RenderSize} window={window.Left},{window.Top},{window.ActualWidth},{window.ActualHeight} work={workArea} dpi={dpi.DpiScaleX},{dpi.DpiScaleY} popup_dpi={VisualTreeHelper.GetDpi(face).DpiScaleX} anchor_dpi={VisualTreeHelper.GetDpi(anchor).DpiScaleX} placement={placement.Point} margin={face.Margin}");
            report.Add($"search_flyout_inside_pane_x={(popupPoint.X-panePoint.X)/dpi.DpiScaleX:0.##} y={(popupPoint.Y-panePoint.Y)/dpi.DpiScaleY:0.##}");
            Check(Math.Abs((popupPoint.X - panePoint.X) / dpi.DpiScaleX - 8) < 1.1
                && Math.Abs((popupPoint.Y - panePoint.Y) / dpi.DpiScaleY - 8) < 1.1,
                "The Search flyout must open eight pixels inside the content pane after leaving the editor.");
            var search = (TextBox)library.FindName("SearchBox");
            search.Text = "absent sample";
            await Task.Delay(350);
            Check(((FrameworkElement)library.FindName("EmptyPanel")).Visibility == Visibility.Visible,
                "Typing in the rail search flyout did not filter the album.");
            search.Clear();
            await Task.Delay(350);
            Check(((FrameworkElement)library.FindName("EmptyPanel")).Visibility != Visibility.Visible,
                "Clearing the rail search did not restore the album.");
            popup.IsOpen = false;
            report.Add("rail_search_opens_and_filters_album=True");
            var promptTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
            promptTimer.Tick += (_, _) =>
            {
                var prompt = App.Instance.Windows.OfType<KillerShot.Controls.PromptDialog>().FirstOrDefault();
                if (prompt == null) return;
                promptTimer.Stop();
                ((TextBox)prompt.FindName("InputText")).Text = "From rail";
                ((Button)prompt.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            promptTimer.Start();
            try { ((Button)window.FindName("NewCollectionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
            finally { promptTimer.Stop(); }
            Check(CaptureLibrary.Collections().Any(collection => collection.Name == "From rail"),
                "The separate New collection rail button did not create the requested collection.");
            report.Add("rail_new_collection_uses_existing_prompt=True");
            code = 0;
        }
        catch (Exception error) { report.Add(error.ToString()); }
        finally
        {
            window?.Close();
            CaptureLibrary.Close();
            if (Directory.Exists(folder)) File.WriteAllLines(Path.Combine(folder, "album-layout-report.txt"), report);
            App.Instance.Shutdown(code);
        }
    }

    private static void Layout(FrameworkElement root)
    {
        root.UpdateLayout();
    }

    private static void Render(FrameworkElement root, string path)
    {
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        ImageOutput.SavePng(image, path);
    }

    private static IEnumerable<DependencyObject> Visuals(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Visuals(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
