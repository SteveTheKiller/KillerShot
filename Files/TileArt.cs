using System.Windows;
using System.Windows.Controls;

namespace KillerShot.Files;

public static class TileArt
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(string), typeof(TileArt), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached("Size", typeof(int), typeof(TileArt), new PropertyMetadata(0, Changed));
    public static readonly DependencyProperty IsDirectoryProperty = DependencyProperty.RegisterAttached("IsDirectory", typeof(bool), typeof(TileArt), new PropertyMetadata(false, Changed));
    public static string? GetPath(DependencyObject target) => (string?)target.GetValue(PathProperty);
    public static void SetPath(DependencyObject target, string? value) => target.SetValue(PathProperty, value);
    public static int GetSize(DependencyObject target) => (int)target.GetValue(SizeProperty);
    public static void SetSize(DependencyObject target, int value) => target.SetValue(SizeProperty, value);
    public static bool GetIsDirectory(DependencyObject target) => (bool)target.GetValue(IsDirectoryProperty);
    public static void SetIsDirectory(DependencyObject target, bool value) => target.SetValue(IsDirectoryProperty, value);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is Image image) Refresh(image);
    }
    public static async void Refresh(Image image)
    {
        string? path = GetPath(image);
        int size = GetSize(image);
        bool directory = GetIsDirectory(image);
        if (string.IsNullOrEmpty(path) || size <= 0) { image.Source = null; image.ClearValue(UIElement.EffectProperty); return; }
        var art = await IconCache.ForTileAsync(path, size, directory);
        bool Same() => GetPath(image) == path && GetSize(image) == size && GetIsDirectory(image) == directory;
        if (!Same()) return;
        Show(image, art.Image, art.IsThumbnail);
        if (art.Late == null) return;
        var late = await art.Late;
        if (late != null && Same()) Show(image, late, true);
    }

    // Only a real picture casts the shadow; a type icon (a video with no thumbnail handler, or one
    // still decoding) sits flat like every other file's.
    private static void Show(Image image, System.Windows.Media.ImageSource? art, bool picture)
    {
        image.Source = art;
        if (picture) image.SetResourceReference(UIElement.EffectProperty, "ThumbShadowEffect");
        else image.ClearValue(UIElement.EffectProperty);
    }
}
