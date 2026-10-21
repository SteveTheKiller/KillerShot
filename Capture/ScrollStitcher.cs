using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerShot.Capture;

/// <summary>
/// Builds one long picture from repeated grabs of a region while the user scrolls it. Each new
/// frame is matched against the previous one: rows (or, for sideways scrolling, columns) that stay
/// in place at the start and end are a sticky header and footer, kept once; the band between them
/// is searched for how far it moved, and only the newly revealed lines are appended. The direction
/// is fixed by the first movement. Pure pixels, no screen access, so it can be tested exactly.
/// </summary>
internal sealed class ScrollStitcher
{
    /// <summary>The share of overlapping lines that must match for a shift to count, so a blinking
    /// cursor or a small animation in the page does not lose track.</summary>
    private const double MatchShare = 0.85;

    private readonly int _width, _height;
    private readonly long _maxPixels;
    private byte[]? _previous;
    private byte[]? _first;
    private bool? _vertical;
    private int _lead, _trail;            // sticky lines at the start and end, fixed at the first move
    private readonly List<byte[]> _content = [];   // the scrolled band, line by line, in order
    private byte[]? _last;

    public ScrollStitcher(int width, int height, long maxPixels)
    {
        _width = width;
        _height = height;
        _maxPixels = maxPixels;
    }

    /// <summary>Lines the content band has grown by so far, for progress.</summary>
    public int Added { get; private set; }
    /// <summary>The picture reached the size limit; later frames are ignored.</summary>
    public bool Full { get; private set; }
    /// <summary>A frame moved too far, or too differently, to match; the result keeps what matched.</summary>
    public bool LostTrack { get; private set; }
    public bool Moved => _vertical != null;

    /// <summary>Adds a frame, BGRA top-down, width * height * 4 bytes.</summary>
    public void Add(byte[] frame)
    {
        if (Full) return;
        var copy = (byte[])frame.Clone();
        if (_previous == null) { _previous = _first = _last = copy; return; }
        if (copy.AsSpan().SequenceEqual(_previous)) return;
        if (_vertical == null)
        {
            // The first movement decides the direction and the sticky bands.
            foreach (bool vertical in new[] { true, false })
            {
                var (lead, trail) = StickyBands(_previous, copy, vertical);
                int lines = vertical ? _height : _width;
                if (lead + trail >= lines - 2) continue;
                int shift = FindShift(_previous, copy, vertical, lead, trail);
                if (shift <= 0) continue;
                _vertical = vertical;
                _lead = lead;
                _trail = trail;
                for (int i = _lead; i < lines - _trail; i++) _content.Add(Line(_first!, vertical, i));
                Append(copy, shift);
                return;
            }
            return;   // nothing matched yet: keep waiting for a clear first move
        }
        int move = FindShift(_previous, copy, _vertical.Value, _lead, _trail);
        if (move > 0) { Append(copy, move); LostTrack = false; }
        else if (move < 0) LostTrack = true;
        // move == 0: the user scrolled back or nothing in the band moved; nothing to add.
    }

    private void Append(byte[] frame, int shift)
    {
        bool vertical = _vertical!.Value;
        int lines = vertical ? _height : _width, across = vertical ? _width : _height;
        int end = lines - _trail;
        for (int i = end - shift; i < end; i++)
        {
            if ((long)(_lead + _content.Count + 1 + _trail) * across > _maxPixels) { Full = true; break; }
            _content.Add(Line(frame, vertical, i));
            Added++;
        }
        _previous = frame;
        _last = frame;
    }

    /// <summary>The stitched picture so far: header from the first frame, the scrolled band, and the
    /// footer from the last frame. Before any movement it is just the first frame.</summary>
    public BitmapSource Result()
    {
        if (_first == null) throw new InvalidOperationException("No frame was captured.");
        if (_vertical == null) return Bitmap(_first, _width, _height);
        bool vertical = _vertical.Value;
        int lines = vertical ? _height : _width;
        var all = new List<byte[]>(_lead + _content.Count + _trail);
        for (int i = 0; i < _lead; i++) all.Add(Line(_first, vertical, i));
        all.AddRange(_content);
        for (int i = lines - _trail; i < lines; i++) all.Add(Line(_last!, vertical, i));
        int across = vertical ? _width : _height, count = all.Count;
        int w = vertical ? _width : count, h = vertical ? count : _height;
        var px = new byte[(long)w * h * 4];
        for (int n = 0; n < count; n++)
        {
            var line = all[n];
            if (vertical) Buffer.BlockCopy(line, 0, px, n * w * 4, across * 4);
            else for (int y = 0; y < across; y++) Buffer.BlockCopy(line, y * 4, px, (y * w + n) * 4, 4);
        }
        return Bitmap(px, w, h);
    }

    private static BitmapSource Bitmap(byte[] px, int w, int h)
    {
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        bmp.Freeze();
        return bmp;
    }

    private byte[] Line(byte[] frame, bool vertical, int index)
    {
        if (vertical)
        {
            var row = new byte[_width * 4];
            Buffer.BlockCopy(frame, index * _width * 4, row, 0, row.Length);
            return row;
        }
        var column = new byte[_height * 4];
        for (int y = 0; y < _height; y++) Buffer.BlockCopy(frame, (y * _width + index) * 4, column, y * 4, 4);
        return column;
    }

    private ulong[] Hashes(byte[] frame, bool vertical)
    {
        int lines = vertical ? _height : _width;
        var hashes = new ulong[lines];
        for (int i = 0; i < lines; i++)
        {
            ulong h = 1469598103934665603UL;
            if (vertical)
            {
                int start = i * _width * 4;
                for (int b = start; b < start + _width * 4; b++) { h ^= frame[b]; h *= 1099511628211UL; }
            }
            else
                for (int y = 0; y < _height; y++)
                {
                    int at = (y * _width + i) * 4;
                    for (int b = at; b < at + 4; b++) { h ^= frame[b]; h *= 1099511628211UL; }
                }
            hashes[i] = h;
        }
        return hashes;
    }

    /// <summary>Lines identical at the same place in both frames, from the start and from the end.</summary>
    private (int Lead, int Trail) StickyBands(byte[] a, byte[] b, bool vertical)
    {
        ulong[] ha = Hashes(a, vertical), hb = Hashes(b, vertical);
        int n = ha.Length, lead = 0, trail = 0;
        while (lead < n && ha[lead] == hb[lead]) lead++;
        while (trail < n - lead && ha[n - 1 - trail] == hb[n - 1 - trail]) trail++;
        return (lead, trail);
    }

    /// <summary>How many lines the band between the sticky bands moved forward: positive when new
    /// content came in at the end, 0 when it did not move forward, -1 when nothing fits.</summary>
    private int FindShift(byte[] previous, byte[] current, bool vertical, int lead, int trail)
    {
        ulong[] hp = Hashes(previous, vertical), hc = Hashes(current, vertical);
        int start = lead, end = hp.Length - trail, band = end - start;
        if (band < 4) return -1;
        int best = 0;
        double bestShare = 0;
        // A page-down scroll leaves only a little overlap, but at least a few lines must overlap
        // for a shift to be told apart from chance.
        int minimumOverlap = Math.Max(8, band / 30);
        for (int shift = 0; shift <= band - minimumOverlap; shift++)
        {
            int overlap = band - shift, same = 0;
            for (int i = 0; i < overlap; i++) if (hc[start + i] == hp[start + shift + i]) same++;
            double share = (double)same / overlap;
            if (share > bestShare + 1e-9) { bestShare = share; best = shift; }
            if (share == 1 && shift > 0) break;   // exact, and the smallest such move
        }
        if (bestShare < MatchShare) return -1;
        return best;
    }
}
