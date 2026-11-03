using System.ComponentModel;
using System.IO;
using System.Globalization;

namespace KillerShot.Files;

/// <summary>A file-system row, with the metadata returned by its directory enumeration.</summary>
public sealed class FileEntry : INotifyPropertyChanged
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string NameLabel => FileName;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTime Modified { get; set; }
    public DateTime Created { get; set; }
    public int Seq { get; set; }
    private string? _typeName;
    public string TypeName => _typeName ??= ShellTypeNames.For(FilePath, IsDirectory);
    internal void PrimeTypeName() => _typeName = ShellTypeNames.For(FilePath, IsDirectory);
    public string ModifiedLabel => DateLabel(Modified);
    public string CreatedLabel => DateLabel(Created);
    public int ModifiedGroup => DateGroups.KeyFor(Modified);
    public int CreatedGroup => DateGroups.KeyFor(Created);
    public string FolderWrapped => Folder.Replace("\\", "\\" + (char)0x200B);
    private System.Windows.Media.ImageSource? _icon;
    private bool _iconRequested;
    private int _iconRevision;
    public System.Windows.Media.ImageSource? Icon
    {
        get
        {
            if (!_iconRequested) { _iconRequested = true; _ = LoadIcon(); }
            return _icon;
        }
    }
    public override string ToString() => NameLabel;

    public string SizeLabel
    {
        get
        {
            long bytes = SizeBytes;
            if (bytes <= 0) return string.Empty;
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0") + " KB";
            double mb = kb / 1024.0;
            return mb < 1024 ? mb.ToString("0.0") + " MB" : (mb / 1024.0).ToString("0.00") + " GB";
        }
    }

    private static string DateLabel(DateTime date) => date == default ? string.Empty : date.ToString("g", CultureInfo.CurrentCulture);
    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyRename(string path, string name, string? typeName = null)
    {
        FilePath = path;
        FileName = name;
        _typeName = typeName;
        ++_iconRevision;
        _icon = null;
        _iconRequested = false;
        foreach (string property in new[] { nameof(FilePath), nameof(FileName), nameof(NameLabel), nameof(TypeName), nameof(Icon) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private async Task LoadIcon()
    {
        int revision = _iconRevision;
        try
        {
            var icon = await IconCache.ForAsync(FilePath, 32, IsDirectory);
            if (revision != _iconRevision) return;
            _icon = icon;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
    }
}
