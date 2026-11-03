using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KillerShot.Files;

public sealed class ResultsViewState : INotifyPropertyChanged
{
    // The sizes the shell can actually serve well. 32 and 48 map to real shell icon sizes;
    // above that comes from the jumbo list (see Services/IconCache.cs).
    public static readonly int[] Steps = [32, 48, 64, 96, 128, 192, 256];

    private int _tileSize = 96;

    public int TileSize
    {
        get => _tileSize;
        set
        {
            int v = Math.Max(Steps[0], Math.Min(Steps[^1], value));
            if (v == _tileSize) return;
            _tileSize = v;
            Notify();
            Notify(nameof(TileWidth));
            Notify(nameof(TileHeight));
        }
    }

    // Room for the art plus two wrapped lines of filename. The width floor keeps small
    // icon sizes from squeezing names down to three characters and an ellipsis.
    //
    // The added number is the horizontal gap between tiles: at 96px art and the Comfortable
    // +16 that is a 112px cell, so icons sit 16px apart. It used to be a flat +44, which
    // spread the grid out with far more air between columns than Explorer uses. Longer
    // names trim sooner as a result - that is the trade, and the tooltip still carries the
    // full path. Density drives it now, so tightening pulls the columns together instead of
    // leaving the same gaps around smaller tiles.
    public double TileWidth  => Math.Max(96, _tileSize + TileExtraW[_density]);

    // The trailing number is everything under the art: the tile's own vertical padding, the
    // gap above the name, and two lines of it. Those three shrink with density, so the cell
    // has to shrink by the same amount or the space just moves from inside the tile to
    // between the rows and nothing looks any tighter.
    public double TileHeight => _tileSize + TileExtraH[_density];

    // ═══════════════════════════════════════════════════════════
    //  ROW ICONS
    // ═══════════════════════════════════════════════════════════
    // Ctrl+wheel is the same gesture in all three views, but a row is a line of text with a
    // picture on it, so it gets its own much shorter ladder: past 64px the icon stops being
    // a marker and starts setting the row height, which is what the tile grid is for. One
    // value drives both row layouts, so a size picked in the cards is still there in
    // details - the way TileSize carries across tabs.
    public static readonly int[] RowIconSteps = [16, 20, 24, 32, 40, 48, 64];

    private int _rowIconSize = 16;

    public int RowIconSize
    {
        get => _rowIconSize;
        set
        {
            int v = Math.Max(RowIconSteps[0],
                             Math.Min(RowIconSteps[^1], value));
            if (v == _rowIconSize) return;
            _rowIconSize = v;
            Notify();
            Notify(nameof(CardIconSize));
            Notify(nameof(RowIconColumn));
            Notify(nameof(ListItemWidth));
            Notify(nameof(ListItemHeight));
        }
    }

    /// <summary>The list card's icon: two px up on the details row's, as it always was.</summary>
    public int CardIconSize => _rowIconSize + 2;
    public double ListItemWidth => 220 + _rowIconSize - 16;
    public double ListItemHeight => _rowIconSize + 6;

    /// <summary>
    /// The details row's icon column - the art plus the gap to the name. Duplicated into
    /// DetailsHeader, which has to keep the same width or every row sits off its heading.
    /// </summary>
    public GridLength RowIconColumn => new(_rowIconSize + 10);

    // ═══════════════════════════════════════════════════════════
    //  DETAILS COLUMNS
    // ═══════════════════════════════════════════════════════════
    // EVERY column is a pixel width, and a star FILLER sits after the last one to soak up
    // whatever is left over. That is what a details list is, and copying it exactly is the
    // point: a divider resizes the column to its LEFT and nothing else, and the columns to
    // the right of it simply slide. One drag, one column.
    //
    // The first two attempts made Name a star column so it would absorb, which meant a drag
    // always had to move a SECOND column to keep the arithmetic balanced - and when that
    // second column hit its floor the overflow landed on Name and the whole row shifted
    // sideways. Both times the complaint was the same and correct: something moved that was
    // not the thing being dragged. A filler column has no opinion, so nothing has to.
    //
    // These live here rather than on the tab because the header Grid and every row's Grid
    // need the same values, and a DataTemplate's Grid can only reach a shared source. That
    // is also why the header's ColumnDefinitions and the row template's have to be kept in
    // step by hand (FilePane.xaml says so in both places).

    internal const double ColMinWidth = 44;
    internal const double ColMaxWidth = 900;

    internal const double DefaultNameWidth     = 260;
    internal const double DefaultLocationWidth = 240;
    internal const double DefaultSizeWidth     = 86;
    internal const double DefaultModifiedWidth = 128;
    internal const double DefaultTypeWidth     = 150;
    internal const double DefaultCreatedWidth  = 128;

    private double _namePx     = DefaultNameWidth;
    private double _locationPx = DefaultLocationWidth;
    private double _sizePx     = DefaultSizeWidth;
    private double _modifiedPx = DefaultModifiedWidth;
    private double _typePx     = DefaultTypeWidth;
    private double _createdPx  = DefaultCreatedWidth;

    // Every width the header and the rows bind to. A change to any one column's width or
    // visibility moves the fit factor, and the fit factor is in all of them.
    private void NotifyColumnWidths()
    {
        Notify(nameof(NameWidth));
        Notify(nameof(LocationWidth));
        Notify(nameof(SizeWidth));
        Notify(nameof(ModifiedWidth));
        Notify(nameof(TypeWidth));
        Notify(nameof(CreatedWidth));
    }

    // Zero while browsing, where the location repeats the folder you are already standing in
    // on every single row; restored for search results, which is the one case where rows come
    // from different places and the column earns its width. The dragged width is remembered
    // across that, so coming back from a folder listing does not reset it.
    private bool _locationHidden;

    public bool LocationHidden
    {
        get => _locationHidden;
        set
        {
            if (_locationHidden == value) return;
            _locationHidden = value;
            Notify();
            Notify(nameof(LocationWidth));
            Notify(nameof(LocationGripVisibility));

            // The other three share out the width the location column just gave up or took
            // back, so the fit factor changes for all of them.
            Notify(nameof(NameWidth));
            Notify(nameof(SizeWidth));
            Notify(nameof(ModifiedWidth));
            Notify(nameof(TypeWidth));
            Notify(nameof(CreatedWidth));
        }
    }

    /// <summary>The location column's resize grip, which follows the column out of sight.</summary>
    /// <remarks>
    /// A Visibility rather than a bool plus a converter: it is the only place in this file
    /// that would need one, and a converter resource exists to be reused.
    /// </remarks>
    public Visibility LocationGripVisibility =>
        _locationHidden ? Visibility.Collapsed : Visibility.Visible;

    // User-toggleable via the details header's right-click menu (ResultsView.cs
    // DetailsHeader_MouseRightButtonUp, Services/ColumnVisibilityMenu.cs) - unlike
    // LocationHidden above, which the app itself drives by context (browsing vs. search).
    // Name is deliberately NOT toggleable here, the same way Explorer's own column chooser
    // never lets you hide Name: it is the row's identity, not a fact about it.
    private bool _sizeVisible = true;

    public bool SizeVisible
    {
        get => _sizeVisible;
        set
        {
            if (_sizeVisible == value) return;
            _sizeVisible = value;
            Notify();
            Notify(nameof(SizeWidth));
            Notify(nameof(NameWidth));
            Notify(nameof(LocationWidth));
            Notify(nameof(ModifiedWidth));
            Notify(nameof(TypeWidth));
            Notify(nameof(CreatedWidth));
        }
    }

    private bool _modifiedVisible = true;

    public bool ModifiedVisible
    {
        get => _modifiedVisible;
        set
        {
            if (_modifiedVisible == value) return;
            _modifiedVisible = value;
            Notify();
            Notify(nameof(ModifiedWidth));
            Notify(nameof(NameWidth));
            Notify(nameof(LocationWidth));
            Notify(nameof(SizeWidth));
            Notify(nameof(TypeWidth));
            Notify(nameof(CreatedWidth));
        }
    }

    private bool _typeVisible = true;

    public bool TypeVisible
    {
        get => _typeVisible;
        set
        {
            if (_typeVisible == value) return;
            _typeVisible = value;
            Notify();
            Notify(nameof(TypeGripVisibility));
            NotifyColumnWidths();
        }
    }

    // A hidden column is zero wide but its grip is not, and a grip left behind would sit on
    // top of its neighbor's and resize a column nobody can see.
    public Visibility TypeGripVisibility =>
        _typeVisible ? Visibility.Visible : Visibility.Collapsed;

    public Visibility CreatedGripVisibility =>
        _createdVisible ? Visibility.Visible : Visibility.Collapsed;

    // Off until asked for, the way Explorer ships it: most listings are read by when a file
    // last changed, and a second date column costs the name its width.
    private bool _createdVisible;

    public bool CreatedVisible
    {
        get => _createdVisible;
        set
        {
            if (_createdVisible == value) return;
            _createdVisible = value;
            Notify();
            Notify(nameof(CreatedGripVisibility));
            NotifyColumnWidths();
        }
    }

    // Date groups (Today, Yesterday, Last week, ...) over a details listing sorted by a date
    // column. Read by ApplySort (Results.cs), which is where a change takes effect - the
    // window listens for this property and re-sorts the pane's tabs (InitResultsView).
    private bool _dateGroups = true;

    public bool DateGroups
    {
        get => _dateGroups;
        set
        {
            if (_dateGroups == value) return;
            _dateGroups = value;
            Notify();
        }
    }

    // ── Fitting the columns to the pane ──────────────────────
    // The widths above are what the USER set. What gets drawn is those scaled down when the
    // pane is too narrow to hold them, so the last column can never be cut off the right
    // edge - the rows do not scroll horizontally, so off the edge means gone, not reachable.
    //
    // Scaled rather than clamped: taking the shortfall out of one column would make the
    // window robbing whichever column happened to be last, and the proportions you chose
    // would come back wrong when it widened again. A factor keeps the relative widths and
    // is perfectly reversible - drag the window back out and the columns return to exactly
    // the widths you set, because nothing was written back.
    private double _availableWidth;

    public double AvailableWidth
    {
        set
        {
            if (Math.Abs(_availableWidth - value) < 0.5) return;
            _availableWidth = value;
            Notify(nameof(NameWidth));
            Notify(nameof(LocationWidth));
            Notify(nameof(SizeWidth));
            Notify(nameof(ModifiedWidth));
            Notify(nameof(TypeWidth));
            Notify(nameof(CreatedWidth));
        }
    }

    /// <summary>1 when everything fits; below 1 when the pane is too narrow.</summary>
    private double FitFactor
    {
        get
        {
            if (_availableWidth <= 0) return 1;              // not measured yet
            double want = _namePx
                        + (_sizeVisible ? _sizePx : 0)
                        + (_modifiedVisible ? _modifiedPx : 0)
                        + (_typeVisible ? _typePx : 0)
                        + (_createdVisible ? _createdPx : 0)
                        + (_locationHidden ? 0 : _locationPx);
            if (want <= _availableWidth || want <= 0) return 1;
            return _availableWidth / want;
        }
    }

    // A floor per column even after scaling, so a very narrow pane leaves something to read
    // rather than four slivers. Below this the last column does get clipped, which at that
    // width is unavoidable and is what Explorer does too.
    private double Fit(double px) => Math.Max(24, px * FitFactor);

    public GridLength NameWidth     => new(Fit(_namePx));
    public GridLength LocationWidth => _locationHidden ? new GridLength(0) : new GridLength(Fit(_locationPx));
    public GridLength SizeWidth     => _sizeVisible ? new GridLength(Fit(_sizePx)) : new GridLength(0);
    public GridLength ModifiedWidth => _modifiedVisible ? new GridLength(Fit(_modifiedPx)) : new GridLength(0);
    public GridLength TypeWidth     => _typeVisible ? new GridLength(Fit(_typePx)) : new GridLength(0);
    public GridLength CreatedWidth  => _createdVisible ? new GridLength(Fit(_createdPx)) : new GridLength(0);

    /// <summary>Set one column's width, clamped. Returns what it actually became.</summary>
    /// <remarks>
    /// Indexed by the column's Grid.Column so the drag handler can carry one number from the
    /// grip's Tag rather than a switch at every call site.
    /// </remarks>
    public double SetColumnWidth(int column, double px)
    {
        px = Math.Max(ColMinWidth, Math.Min(ColMaxWidth, px));
        switch (column)
        {
            case 1: _namePx     = px; Notify(nameof(NameWidth));     break;
            case 2: _locationPx = px; Notify(nameof(LocationWidth)); break;
            case 3: _sizePx     = px; Notify(nameof(SizeWidth));     break;
            case 4: _modifiedPx = px; Notify(nameof(ModifiedWidth)); break;
            case 5: _typePx     = px; Notify(nameof(TypeWidth));     break;
            case 6: _createdPx  = px; Notify(nameof(CreatedWidth));  break;
        }
        return px;
    }

    /// <summary>The current width of one column, for seeding a drag.</summary>
    public double GetColumnWidth(int column) => column switch
    {
        1 => _namePx,
        2 => _locationPx,
        3 => _sizePx,
        5 => _typePx,
        6 => _createdPx,
        _ => _modifiedPx,
    };

    /// <summary>Back to the shipped width. Double-clicking a divider does this.</summary>
    public static double DefaultColumnWidth(int column) => column switch
    {
        1 => DefaultNameWidth,
        2 => DefaultLocationWidth,
        3 => DefaultSizeWidth,
        5 => DefaultTypeWidth,
        6 => DefaultCreatedWidth,
        _ => DefaultModifiedWidth,
    };

    /// <summary>
    /// What every column EXCEPT <paramref name="column"/> is taking up right now.
    /// </summary>
    /// <remarks>
    /// The drag uses this to stop the columns growing past the pane. Without a ceiling the
    /// last one runs off the right edge and is simply cut off - the rows are not horizontally
    /// scrollable, so anything past the edge is gone rather than reachable. A hidden location
    /// column counts as nothing, which is what it is.
    /// </remarks>
    public double TotalWidthExcept(int column)
    {
        double t = 0;
        if (column != 1) t += _namePx;
        if (column != 2 && !_locationHidden) t += _locationPx;
        if (column != 3 && _sizeVisible) t += _sizePx;
        if (column != 4 && _modifiedVisible) t += _modifiedPx;
        if (column != 5 && _typeVisible) t += _typePx;
        if (column != 6 && _createdVisible) t += _createdPx;
        return t;
    }

    // ═══════════════════════════════════════════════════════════
    //  DENSITY
    // ═══════════════════════════════════════════════════════════
    // 0 Roomy, 1 Comfortable, 2 Compact, 3 Tight, 4 Minimal. Every view's padding is derived
    // here rather than hardcoded in its template, so one property change retightens all
    // three at once.
    //
    // Exposed as properties on this shared object rather than stamped onto every result the
    // way KillerNotes stamps its notes: KillerShell's list can hold six figures of rows and
    // walking them to set a padding would be absurd, while the templates already bind here
    // for the tile size. Changing a property repaints; nothing is re-listed.
    /// <summary>Number of density levels. The cycle and the status captions both key off it.</summary>
    public const int DensityLevels = 5;

    // Comfortable, not Roomy, is where a fresh install lands: it is the spacing every
    // screenshot and every previous build used, and level 0 exists to go LOOSER than that.
    private int _density = 1;

    public int Density
    {
        get => _density;
        set
        {
            int v = value < 0 ? 0 : value > DensityLevels - 1 ? DensityLevels - 1 : value;
            if (v == _density) return;
            _density = v;

            // Everything derived, in one go. TileHeight is in here because a tighter tile is
            // a SHORTER cell, not just a smaller picture - without it the grid keeps the old
            // row pitch and the padding comes off the inside of an unchanged box.
            Notify();
            Notify(nameof(TilePad));
            Notify(nameof(TileMargin));
            Notify(nameof(TileNamePad));
            Notify(nameof(TileWidth));
            Notify(nameof(TileHeight));
            Notify(nameof(RowPad));
            Notify(nameof(CardPad));
            Notify(nameof(HeaderPad));
        }
    }

    // The ladder, one row per level, written as tables rather than switch arms: with five
    // levels the point of the numbers is how they step, and a column you can read down
    // catches a value out of order in a way five separate expressions never would.
    //
    // Index: 0 Roomy, 1 Comfortable, 2 Compact, 3 Tight, 4 Minimal.

    // Tiles. The name keeps its two lines at every level - trimming a file name to one line
    // is lost information, which is the opposite of what density is for.
    private static readonly double[] TileExtraW = [30, 16, 12, 8, 4];
    private static readonly double[] TileExtraH = [62, 52, 46, 40, 34];

    private static readonly Thickness[] TilePads =
    [
        new(6, 8, 6, 8), new(4, 6, 4, 6), new(3, 4, 3, 4),
        new(2, 2, 2, 2), new(1, 1, 1, 1),
    ];

    private static readonly Thickness[] TileMargins =
    [
        new(6), new(3), new(2), new(1), new(0),
    ];

    private static readonly Thickness[] TileNamePads =
    [
        new(2, 8, 2, 0), new(2, 6, 2, 0), new(2, 4, 2, 0),
        new(2, 2, 2, 0), new(2, 1, 2, 0),
    ];

    public Thickness TilePad     => TilePads[_density];
    public Thickness TileMargin  => TileMargins[_density];
    public Thickness TileNamePad => TileNamePads[_density];

    // Details rows and list cards. The side padding moves with density too, so a tight level
    // wins width as well as height - but the right number never drops below 22, because
    // that gap is what keeps the last column out from under the scrollbar rather than
    // decoration. The left number is shared with the column headers, which is why HeaderPad
    // exists: the two have to step together or every row sits off its own heading.
    private static readonly Thickness[] RowPads =
    [
        new(20, 5, 36, 5), new(14, 3, 30, 3), new(10, 1, 26, 1),
        new(8, 1, 24, 1),  new(6, 0, 22, 0),
    ];

    private static readonly Thickness[] CardPads =
    [
        new(20, 9, 20, 9), new(14, 6, 14, 6), new(10, 4, 10, 4),
        new(8, 3, 8, 3),   new(6, 2, 6, 2),
    ];

    // The header's own vertical padding is fixed - it is a band of type, not a row of
    // results, and letting it shrink would just make the column names harder to hit.
    private static readonly Thickness[] HeaderPads =
    [
        new(20, 1, 36, 1), new(14, 1, 30, 1), new(10, 1, 26, 1),
        new(8, 1, 24, 1),  new(6, 1, 22, 1),
    ];

    public Thickness RowPad    => RowPads[_density];
    public Thickness CardPad   => CardPads[_density];
    public Thickness HeaderPad => HeaderPads[_density];

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
