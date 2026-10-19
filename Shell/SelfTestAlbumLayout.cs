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
            Settings.Set("StripWidth", "340");
            Settings.Set("AlbumStripWidth", "180");
            Settings.SetBool("StripMinimized", false);
            if (!CaptureLibrary.OpenAuto()) throw new InvalidOperationException("Synthetic album did not open.");
            var image = BitmapSource.Create(168, 104, 96, 96, PixelFormats.Bgra32, null,
                Enumerable.Repeat((byte)255, 168 * 104 * 4).ToArray(), 168 * 4);
            image.Freeze();
            CaptureLibrary.AddNow(image, DateTime.Today, "region", "Layout sample", null);
            CaptureLibrary.AddNow(image, DateTime.Today, "region", "Second layout sample", null);
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

            var dock = window.TestNavigationDock;
            var pane = (FrameworkElement)dock.Pane!;
            Check(dock.ColumnDefinitions.Count == 3 && dock.ColumnDefinitions[1].ActualWidth == 0,
                "The resize target must overlap the pane boundary without reserving a blank column.");
            Check(Math.Abs(pane.ActualWidth - 180) < 1, "Album navigation must restore its own saved width.");
            var browser = Visuals(pane).OfType<ListBox>().Single();
            Check(browser.Items.Count == 3, "Album navigation must list All captures, the named collection, and Not in a collection.");
            for (int attempt = 0; attempt < 40 && !Visuals(browser).OfType<Image>().Any(photo => photo.Source != null); attempt++)
                await Task.Delay(25);
            var loadedPreviews = Visuals(browser).OfType<Image>().Where(photo => photo.Source != null)
                .Select(photo => (Photo: photo, Source: photo.Source)).ToArray();
            Check(loadedPreviews.Length > 0, "The collection selection check needs loaded photo previews.");
            browser.SelectedIndex = 1;
            Layout(root);
            Check(loadedPreviews.All(preview => Visuals(browser).Contains(preview.Photo)
                && ReferenceEquals(preview.Photo.Source, preview.Source)),
                "Selecting a collection must keep loaded sidebar photos in place instead of rebuilding them.");
            await Task.Delay(250);
            Check(window.TestStrip!.Source == "c" + collectionId && App.Instance.Document == null,
                "Selecting a collection must filter the album without opening a photo.");
            library.SetView("flow");
            var mainFlow = (KillerShot.Controls.CoverFlow)library.FindName("BigFlow");
            mainFlow.SelectedIndex = 1;
            long? selectedPhoto = mainFlow.SelectedItem?.Id;
            App.Instance.SetDocument(new EditorDocument(image));
            var openDocument = App.Instance.Document;
            var photoDock = window.TestStripDock;
            window.TestNavigationStrip!.PreviewMenu().Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "pile"))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            foreach (var sidebar in new[] { DockSide.Left, DockSide.Right })
            foreach (var carousel in new[] { DockSide.Top, DockSide.Bottom })
            {
                dock.DockTo(sidebar);
                photoDock.DockTo(carousel);
                Layout(root);
                var photos = (FrameworkElement)photoDock.Pane!;
                var imageView = (FrameworkElement)window.FindName("EditorScroll");
                var photoBounds = photos.TransformToAncestor(root).TransformBounds(new Rect(photos.RenderSize));
                var imageBounds = imageView.TransformToAncestor(root).TransformBounds(new Rect(imageView.RenderSize));
                Check(pane.IsVisible && ReferenceEquals(App.Instance.Document, openDocument)
                    && window.TestNavigationStrip!.Source == "c" + collectionId
                    && ((KillerShot.Controls.CoverFlow)window.TestStrip!.TestCarousel).Items.Count == 2,
                    "Editor placement must retain the collection sidebar, filtered photo carousel, and document.");
                Check(carousel == DockSide.Top ? photoBounds.Bottom <= imageBounds.Top : photoBounds.Top >= imageBounds.Bottom,
                    "The photo carousel must sit above or below the large editor.");
                Check(Math.Abs(pane.ActualWidth - 180) < 1, "Editor mode must keep the collection sidebar's width.");
                foreach (var button in ((Panel)window.FindName("RailStack")).Children.OfType<Button>().Where(button => button.IsVisible))
                {
                    var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
                    var hit = window.InputHitTest(point) as DependencyObject;
                    while (hit != null && !ReferenceEquals(hit, button)) hit = VisualTreeHelper.GetParent(hit);
                    Check(ReferenceEquals(hit, button), "Overflowing collection photos must not cover rail button hit targets.");
                }
                Render(root, Path.Combine(folder, $"editor-{sidebar}-{carousel}.png"));
            }
            dock.DockTo(DockSide.Left);
            photoDock.DockTo(DockSide.Top);
            App.Instance.SetDocument(null);
            Layout(root);
            Check(window.TestStrip.Source == "c" + collectionId && library.CurrentView == "flow"
                && mainFlow.Items.Count == 2 && mainFlow.SelectedItem?.Id == selectedPhoto,
                "Editor navigation must not replace the album's collection, view, or photo selection.");
            var workspaceMenu = pane.ContextMenu;
            workspaceMenu.PlacementTarget = pane;
            workspaceMenu.IsOpen = true;
            var previews = workspaceMenu.Items.OfType<MenuItem>().SingleOrDefault(item =>
                Equals(item.Header, LocaleManager.Loc("Str_CollectionPreview")));
            Check(previews != null && previews.Items.Count == 4, "All four collection preview styles must be available from the sidebar menu.");
            workspaceMenu.IsOpen = false;
            foreach (var choice in previews!.Items.OfType<MenuItem>())
            {
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Layout(root);
                await Task.Delay(180);
                Check(window.TestStrip.Source == "c" + collectionId && library.CurrentView == "flow"
                    && mainFlow.SelectedItem?.Id == selectedPhoto, "Changing collection previews lost the browsing context.");
                if (Equals(choice.Tag, "mini"))
                {
                    var row = (ListBoxItem)browser.ItemContainerGenerator.ContainerFromItem(browser.SelectedItem);
                    var previewImage = Visuals(row).OfType<Image>().Single(image => image.Width == 108);
                    var previousPreview = previewImage.Source;
                    Visuals(row).OfType<Button>().Last().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(!ReferenceEquals(previousPreview, previewImage.Source)
                        && window.TestStrip.Source == "c" + collectionId && ReferenceEquals(App.Instance.Document, null),
                        "Mini-carousel arrows must browse previews without opening a photo or changing collections.");
                    Check(System.Windows.Automation.AutomationProperties.GetName(row).Contains("Layout collection"),
                        "Collection rows must announce their name and count.");
                }
                if (Equals(choice.Tag, "pile"))
                {
                    var row = (ListBoxItem)browser.ItemContainerGenerator.ContainerFromItem(browser.SelectedItem);
                    var centers = Visuals(row).OfType<Image>().Where(image => image.Source != null)
                        .Select(image => image.TransformToAncestor(row).Transform(new Point(image.ActualWidth / 2, image.ActualHeight / 2))).ToArray();
                    Check(centers.Max(p => p.X) - centers.Min(p => p.X) > 30
                        && centers.Max(p => p.Y) - centers.Min(p => p.Y) > 24,
                        "Scattered photos must spread across both dimensions instead of forming an offset stack.");
                }
                var stablePhotos = Visuals(browser).OfType<Image>().Where(photo => photo.Source != null)
                    .Select(photo => (Photo: photo, Source: photo.Source)).ToArray();
                Check(stablePhotos.Length > 0, "Each preview style needs loaded photos before selection.");
                browser.SelectedIndex = 0;
                Layout(root);
                browser.SelectedIndex = 1;
                Layout(root);
                await Task.Delay(100);
                Check(stablePhotos.All(preview => Visuals(browser).Contains(preview.Photo)
                    && ReferenceEquals(preview.Photo.Source, preview.Source)),
                    "Collection selection must preserve loaded photos and mini-carousel position for " + choice.Tag);
                report.Add("collection_selection_preserves_loaded_previews_" + choice.Tag + "=True");
                Render(root, Path.Combine(folder, "collection-preview-" + choice.Tag + ".png"));
            }
            foreach (var theme in new[] { Theme.Dark, Theme.Light, Theme.SE98 }.Concat(Enum.GetValues<Theme>()).Distinct())
            {
                ThemeManager.Apply(theme);
                library.SetView("flow");
                Layout(root);
                await Task.Delay(180);
                Render(root, Path.Combine(folder, "browser-theme-" + theme + ".png"));
                if (theme is Theme.Dark or Theme.Light or Theme.SE98)
                {
                    library.SetView("shelves");
                    Layout(root);
                    Render(root, Path.Combine(folder, "multiple-carousels-pane-" + theme + ".png"));
                    library.SetView("flow");
                    Layout(root);
                }
                Check(mainFlow.ActualWidth > 0 && mainFlow.ActualHeight > 0 && browser.IsVisible,
                    "The collection browser and main photo must remain available in " + theme);
            }
            ThemeManager.Apply(Theme.Dark);
            LocaleManager.Apply(Locale.De);
            CaptureLibrary.RenameCollection(collectionId, "A long collection name that must remain inside the narrow navigation row without covering its count or the rail");
            window.Width = 700;
            Layout(root);
            await Task.Delay(180);
            Render(root, Path.Combine(folder, "browser-narrow-German.png"));
            window.Width = Math.Min(1000, workArea.Width - 40);
            CaptureLibrary.RenameCollection(collectionId, "Layout collection");
            LocaleManager.Apply(Locale.EnUS);
            Layout(root);
            report.Add("all_themes_main_photo_and_narrow_localized_browser_rendered=True");
            browser.SelectedIndex = 0;
            Layout(root);
            report.Add("album_collection_selection_filters_without_opening=True");
            var chevron = Visuals(dock).OfType<Button>().Single(button => button.Content is string text && text is "\uE76B" or "\uE76C");
            foreach (string view in new[] { "shelves", "flow", "grid", "timeline" })
            {
                library.SetView(view);
                Layout(root);
                var viewPane = (Border)window.FindName("ContentPane");
                Check(ReferenceEquals(viewPane.Background, window.FindResource("ContentPaneBrush")) == (view is "shelves" or "flow"),
                    "Both carousel views must use the main content pane background; grid and timeline keep their existing background.");
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
            Check(dock.Side == DockSide.Left && dock.IsMinimized && !photoDock.IsMinimized,
                "Collection navigation must stay collapsed while the independent editor carousel stays expanded.");
            photoDock.DockTo(DockSide.Bottom);
            photoDock.Minimize();
            App.Instance.SetDocument(null);
            Layout(root);
            Check(dock.Side == DockSide.Left && dock.IsMinimized, "Returning to the album must preserve its own collapsed sidebar.");
            chevron.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            App.Instance.SetDocument(new EditorDocument(image));
            Layout(root);
            Check(!dock.IsMinimized && photoDock.Side == DockSide.Bottom && photoDock.IsMinimized,
                "Expanding navigation must preserve the bottom carousel's collapsed state.");
            report.Add("album_sidebar_chevron_available_in_all_views=True");
            report.Add("album_and_editor_minimized_states_are_independent=True");

            report.Add("Chooser H1: The populated collection picker is absent from the carousel's visual tree, so adding it restores selection.");
            report.Add("Chooser H2: The picker is present but clipped; measuring its visible bounds inside the strip distinguishes clipping.");
            report.Add("Chooser H3: Collection selection is stale; choosing a source must update the strip and its current caption.");
            photoDock.Restore();
            Layout(root);
            var photoPane = (FrameworkElement)photoDock.Pane!;
            var picker = Visuals(photoPane).OfType<ComboBox>().SingleOrDefault();
            Check(picker != null && picker.IsVisible, "The horizontal carousel frame needs its collection chooser.");
            picker!.IsDropDownOpen = true;
            await Task.Delay(100);
            picker.SelectedItem = picker.Items.OfType<ComboBoxItem>().Single(item => item.Tag is string tag && tag == "c" + collectionId);
            picker.IsDropDownOpen = false;
            Layout(root);
            var strip = (CollectionStrip)photoPane;
            Check(strip.Source == "c" + collectionId && strip.SourceCaption == "Layout collection",
                "The carousel chooser did not switch to its selected collection.");
            var pickerBounds = picker.TransformToAncestor(photoPane).TransformBounds(new Rect(picker.RenderSize));
            Check(pickerBounds.Left >= 0 && pickerBounds.Top >= 0 && pickerBounds.Right <= photoPane.ActualWidth
                && pickerBounds.Bottom <= photoPane.ActualHeight, "The carousel chooser is clipped outside its frame.");
            Render(root, Path.Combine(folder, "carousel-collection-chooser.png"));
            report.Add("carousel_collection_chooser_switches_source_inside_frame=True");

            ((Button)window.FindName("LibrarySearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(150);
            var popup = (Popup)library.FindName("SearchPopup");
            report.Add($"search_state open={popup.IsOpen} anchor={(popup.PlacementTarget as FrameworkElement)?.Name} libraryVisible={library.IsVisible} loaded={library.IsLoaded} windowLoaded={window.IsLoaded} windowVisible={window.IsVisible} searchAnchor={library.SearchAnchor?.Name}");
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
            await Task.Delay(200);
            var emptyCollection = CaptureLibrary.Collections().Single(c => c.Name == "From rail");
            var previousDocument = App.Instance.Document;
            browser.SelectedItem = browser.Items.Cast<CollectionChoice>().Single(c => c.Source == "c" + emptyCollection.Id);
            Layout(root);
            Check(window.TestStrip.Source == "c" + emptyCollection.Id && ReferenceEquals(App.Instance.Document, previousDocument)
                && ((FrameworkElement)library.FindName("EmptyPanel")).IsVisible,
                "An empty collection must remain selectable without opening a photo.");
            FolderCaptures.Enabled = true;
            await Task.Delay(200);
            Layout(root);
            Check(browser.Items.Cast<CollectionChoice>().Any(c => c.Source == "folder"), "The enabled Screenshots source is missing.");
            string originalAlbum = CaptureLibrary.FilePath;
            string otherAlbum = CaptureLibrary.CreateAlbum("Navigation fixture");
            Check(CaptureLibrary.TrySwitchAlbum(otherAlbum, null, out _), "The second test album did not open.");
            await Task.Delay(250);
            Layout(root);
            Check(window.TestStrip.Source == "all" && browser.Items.Count == 3
                && browser.Items.Cast<CollectionChoice>().All(c => c.Preview.Count == 0),
                "Switching to an empty album must clear collection previews and selection from the previous album.");
            CaptureLibrary.SetPassword("navigation fixture password");
            CaptureLibrary.Close();
            await Task.Delay(200);
            Layout(root);
            Check(((FrameworkElement)library.FindName("LockPanel")).IsVisible && browser.IsVisible,
                "The locked album must retain usable navigation and its unlock surface.");
            Check(CaptureLibrary.TrySwitchAlbum(originalAlbum, null, out _), "The original test album did not reopen.");
            await Task.Delay(250);
            Layout(root);
            Check(browser.Items.Cast<CollectionChoice>().Any(c => c.Name == "Layout collection"),
                "Returning to the original album did not restore its own collection names.");
            report.Add("empty_collections_folder_source_album_switch_and_locked_album_navigation=True");
            App.Instance.SetDocument(new EditorDocument(image));
            dock.DockTo(DockSide.Left);
            Layout(root);
            App.Instance.SetDocument(null);
            Layout(root);
            Check(Math.Abs(pane.ActualWidth - 180) < 1, "Album width must restore when both modes dock at the left edge.");
            App.Instance.SetDocument(new EditorDocument(image));
            Layout(root);
            Check(Math.Abs(pane.ActualWidth - 180) < 1 && window.TestStripDock.IsHorizontal,
                "The collection sidebar must persist beside the editor's independent horizontal carousel.");
            App.Instance.SetDocument(null);
            for (int i = 0; i < 120; i++) CaptureLibrary.CreateCollection("Navigation collection " + i);
            await App.Instance.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Layout(root);
            Check(browser.Items.Count == 125 && Visuals(browser).OfType<CollectionPreview>().Count() < 50,
                "A large collection list must realize only the visible and nearby preview rows.");
            browser.SelectedIndex = browser.Items.Count - 2;
            browser.ScrollIntoView(browser.SelectedItem);
            Layout(root);
            await Task.Delay(180);
            Check(window.TestStrip.Source != "all" && ReferenceEquals(App.Instance.Document, null),
                "Selecting a distant collection must filter the album without opening a photo.");
            Render(root, Path.Combine(folder, "browser-large-collection-list.png"));
            report.Add("same_edge_width_restoration_and_125_virtualized_collection_rows=True");
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
