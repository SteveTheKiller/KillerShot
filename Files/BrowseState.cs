using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShot.Files;

public sealed class BrowseState : INotifyPropertyChanged
{
    public ObservableCollection<FileEntry> Results { get; } = [];
    public List<string> History { get; } = [];
    public int HistoryIndex { get; internal set; } = -1;
    private string _currentFolder = string.Empty;
    public string CurrentFolder { get => _currentFolder; internal set { _currentFolder = value; Notify(); } }
    public int SortIndex { get; set; } = 4;
    public bool SortAsc { get; set; }
    public string FilterText { get; set; } = string.Empty;
    public bool ShowHidden { get; set; }
    public bool FoldersOnTop { get; set; } = true;
    public bool GroupDates { get; set; }
    public bool CanGoBack => HistoryIndex > 0;
    public bool CanGoForward => HistoryIndex < History.Count - 1;
    private bool _loading;
    public bool IsLoading { get => _loading; internal set { _loading = value; Notify(); } }
    private Exception? _error;
    public Exception? Error { get => _error; internal set { _error = value; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
