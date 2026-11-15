using System.Globalization;
using KillerShot.Services;

namespace KillerShot.Shell;

/// <summary>A linked folder collection and the pictures it lists for the current search.</summary>
internal sealed record LinkedFolderListing(string Source, string Name, IReadOnlyList<CaptureItem> Members);

/// <summary>A database and folder query result, independent of the library's controls.</summary>
internal sealed record LibraryListing(IReadOnlyList<CaptureItem> Items, IReadOnlyList<CaptureCollection> Collections,
    IReadOnlyList<CaptureItem> FolderItems, int Total, bool LinkedPhotos)
{
    /// <summary>Every collection in the album, whatever the listing shows. Collection tabs take
    /// their names from it and fall back to All when theirs is gone.</summary>
    internal IReadOnlyList<CaptureCollection> AlbumCollections { get; init; } = Collections;

    /// <summary>The linked folders with their matching pictures, read off the interface thread
    /// so grouping by collection does not list the folders again.</summary>
    internal IReadOnlyList<LinkedFolderListing> LinkedFolders { get; init; } = [];

    internal static LibraryListing Read(string source, string search)
    {
        var collections = CaptureLibrary.Collections();
        var album = collections;
        string? words = search.Length > 0 ? search : null;
        bool Matches(CaptureItem item) => source == "all" || (source == "c0"
            ? item.CollectionId == null || !collections.Any(c => c.Id == item.CollectionId)
            : "c" + item.CollectionId?.ToString(CultureInfo.InvariantCulture) == source);
        IReadOnlyList<CaptureItem> items;
        int total;
        // Each source reads only what it shows: a folder never queries the album, and only All
        // shows the Screenshots folder beside the collections.
        if (FolderCaptures.IsSource(source))
        {
            items = FolderCaptures.ItemsForSource(source, words);
            total = words != null ? FolderCaptures.ItemsForSource(source).Count : items.Count;
            collections = [];
        }
        else
        {
            items = CaptureLibrary.Items(words).Where(Matches).ToList();
            total = words != null ? CaptureLibrary.CountItems(source) : items.Count;
            if (source != "all") collections = collections.Where(c => "c" + c.Id.ToString(CultureInfo.InvariantCulture) == source).ToList();
        }
        var folderItems = source == "all" ? FolderCaptures.Snapshot(words) : [];
        var linked = FolderCaptures.LinkedCollections()
            .Select(folder => new LinkedFolderListing(folder.Source, folder.Name, FolderCaptures.LinkedItems(folder.Source, words)))
            .ToList();
        bool linkedPhotos = linked.Any(folder => folder.Members.Count > 0);
        return new(items, collections, folderItems, total, linkedPhotos) { AlbumCollections = album, LinkedFolders = linked };
    }
}
