using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using KillerShot.Controls;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>The Image menu: turn, mirror, resize, add a margin and trim a plain border. On the
/// toolbar's Image button and in the editor's right-click menu.</summary>
public partial class MainWindow
{
    private readonly ContextMenu _imageMenu = new();

    private void ImageBtn_Click(object sender, RoutedEventArgs e)
    {
        _imageMenu.Items.Clear();
        foreach (var item in ImageMenuItems()) _imageMenu.Items.Add(item);
        OpenFlyout(_imageMenu, ImageBtn);
    }

    /// <summary>The menu's commands, built fresh so they follow the language and the picture.</summary>
    private IEnumerable<object> ImageMenuItems()
    {
        bool has = _doc != null;
        MenuItem Item(string key, Action action, string gesture = "")
        {
            var item = new MenuItem { Header = Loc(key), InputGestureText = gesture, IsEnabled = has };
            item.Click += (_, _) => { if (_doc != null) { Editor.CommitText(); action(); } };
            return item;
        }
        yield return Item("Str_Img_Paste", PasteIntoPicture, "Ctrl+V");
        yield return Item("Str_Img_InsertFile", InsertImageFile);
        yield return new Separator();
        yield return Item("Str_Img_RotateRight", () => _doc!.Rotate(clockwise: true));
        yield return Item("Str_Img_RotateLeft", () => _doc!.Rotate(clockwise: false));
        yield return Item("Str_Img_FlipH", () => _doc!.Flip(horizontal: true));
        yield return Item("Str_Img_FlipV", () => _doc!.Flip(horizontal: false));
        yield return new Separator();
        var resize = new MenuItem { Header = Loc("Str_Img_Resize"), IsEnabled = has };
        foreach (int percent in new[] { 50, 75, 150, 200 })
        {
            int p = percent;
            var item = new MenuItem { Header = p.ToString(CultureInfo.CurrentCulture) + "%" };
            item.Click += (_, _) => ResizeTo(Math.Max(1, (int)Math.Round(_doc!.PixelWidth * p / 100.0)), Math.Max(1, (int)Math.Round(_doc.PixelHeight * p / 100.0)));
            resize.Items.Add(item);
        }
        resize.Items.Add(new Separator());
        var custom = new MenuItem { Header = Loc("Str_Img_ResizeCustom") };
        custom.Click += (_, _) => PromptResize();
        resize.Items.Add(custom);
        yield return resize;
        yield return Item("Str_Img_Margin", PromptMargin);
        yield return Item("Str_Img_AutoCrop", () =>
        {
            if (!_doc!.AutoCrop()) StatusText.Text = Loc("Str_Status_NoBorder");
        });
    }

    /// <summary>Ctrl+V on an open picture: the clipboard's picture goes on top as an object.</summary>
    private void PasteIntoPicture()
    {
        if (_doc == null) { PasteAsNewPicture(); return; }
        try
        {
            if (ClipboardImages.FromClipboard() is { } image) Editor.InsertImage(image);
            else StatusText.Text = Loc("Str_Status_NoClipboardImage");
        }
        catch (Exception error) when (error is NotSupportedException or System.IO.IOException) { StatusText.Text = error.Message; }
    }

    /// <summary>Ctrl+Shift+V: the clipboard's picture opens as a new picture of its own.</summary>
    private void PasteAsNewPicture()
    {
        try
        {
            if (ClipboardImages.FromClipboard() is not { } image) { StatusText.Text = Loc("Str_Status_NoClipboardImage"); return; }
            if (App.Instance.SetDocument(new KillerShot.Editor.EditorDocument(image)))
            {
                if (_libraryShown) { _libraryShown = false; SyncLibraryVisibility(); }
                StatusText.Text = string.Format(Loc("Str_Status_Pasted"), image.PixelWidth, image.PixelHeight);
            }
        }
        catch (Exception error) when (error is NotSupportedException or System.IO.IOException) { StatusText.Text = error.Message; }
    }

    private void InsertImageFile()
    {
        if (_doc == null) return;
        var dialog = new FileDialog(FileDialogMode.Open) { Title = Loc("Str_Img_InsertFile"), Filter = Loc("Str_Dlg_OpenFilter") };
        if (dialog.ShowDialog(this) != true) return;
        try { Editor.InsertImage(ImageOutput.Load(dialog.FileName)); }
        catch (Exception error) { StatusText.Text = string.Format(Loc("Str_Status_OpenFailed"), error.Message); }
    }

    private void ResizeTo(int width, int height)
    {
        if (_doc == null) return;
        try { _doc.Scale(width, height); }
        catch (ImageTooLargeException error) { StatusText.Text = error.Message; }
    }

    private void PromptResize()
    {
        if (_doc == null) return;
        string current = $"{_doc.PixelWidth} x {_doc.PixelHeight}";
        var dialog = new PromptDialog(Loc("Str_Img_ResizeCustom"), Loc("Str_Img_ResizeDetail"), Loc("Str_Img_Resize"), current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (ParseResize(dialog.Text, _doc.PixelWidth, _doc.PixelHeight) is { } size) ResizeTo(size.Width, size.Height);
        else StatusText.Text = Loc("Str_Img_Invalid");
    }

    /// <summary>"50%" keeps the shape; "1280 x 720" sets both; "1280" alone keeps the shape too. A number
    /// too big for an int becomes int.MaxValue, so Scale refuses it as too large rather than crashing.</summary>
    internal static (int Width, int Height)? ParseResize(string input, int width, int height)
    {
        string text = input.Trim();
        static int Whole(string digits) => int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : int.MaxValue;
        static int Scaled(double value) => (int)Math.Clamp(Math.Round(value), 1, int.MaxValue);
        if (Regex.Match(text, @"^(\d+(?:[.,]\d+)?)\s*%$") is { Success: true } pct
            && double.TryParse(pct.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent) && percent > 0)
            return (Scaled(width * percent / 100), Scaled(height * percent / 100));
        if (Regex.Match(text, @"^(\d+)\s*[xX×*,]\s*(\d+)$") is { Success: true } size)
            return (Whole(size.Groups[1].Value), Whole(size.Groups[2].Value));
        if (Regex.IsMatch(text, @"^\d+$") && Whole(text) is > 0 and int w)
            return (w, Scaled((double)height * w / width));
        return null;
    }

    private void PromptMargin()
    {
        if (_doc == null) return;
        var dialog = new PromptDialog(Loc("Str_Img_Margin"), Loc("Str_Img_MarginDetail"), Loc("Str_Img_MarginOk"), "20") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!int.TryParse(dialog.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < 1 || size > 2000)
        {
            StatusText.Text = Loc("Str_Img_Invalid");
            return;
        }
        try { _doc.AddMargin(size, Editor.Color); }
        catch (ImageTooLargeException error) { StatusText.Text = error.Message; }
    }
}
