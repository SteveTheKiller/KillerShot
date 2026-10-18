namespace KillerTools.Qr;

/// <summary>Reads the format, removes the mask and collects the data codewords from a sampled module grid.</summary>
internal static class MatrixDecoder
{
    private static readonly QrErrorCorrection[] LevelFromBits = [QrErrorCorrection.M, QrErrorCorrection.L, QrErrorCorrection.H, QrErrorCorrection.Q];

    /// <summary>The 32 valid 15-bit format words, indexed by their 5 data bits.</summary>
    private static readonly int[] FormatWords = Enumerable.Range(0, 32).Select(data =>
    {
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        return ((data << 10) | rem) ^ 0x5412;
    }).ToArray();

    public static int? ReadVersion(bool[,] modules)
    {
        int dimension = modules.GetLength(0);
        if (dimension < 45) return (dimension - 17) / 4;
        int first = 0, second = 0;
        for (int bit = 0; bit < 18; bit++)
        {
            int near = dimension - 11 + bit % 3, far = bit / 3;
            if (modules[far, near]) first |= 1 << bit;
            if (modules[near, far]) second |= 1 << bit;
        }
        int best = 0, distance = 4;
        for (int version = 7; version <= 40; version++)
        {
            int remainder = version << 12;
            for (int bit = 17; bit >= 12; bit--)
                if ((remainder & (1 << bit)) != 0) remainder ^= 0x1f25 << (bit - 12);
            int word = (version << 12) | remainder;
            int errors = Math.Min(int.PopCount(word ^ first), int.PopCount(word ^ second));
            if (errors < distance) { distance = errors; best = version; }
        }
        return best != 0 && best * 4 + 17 == dimension ? best : null;
    }

    public static (QrErrorCorrection Level, int Mask)? ReadFormat(bool[,] m)
    {
        int n = m.GetLength(0);
        int first = 0, second = 0;
        // Bit i of the format word, at its two places (x is the column, y the row).
        for (int i = 0; i < 15; i++)
        {
            (int x, int y) a = i < 6 ? (8, i) : i == 6 ? (8, 7) : i == 7 ? (8, 8) : i == 8 ? (7, 8) : (14 - i, 8);
            (int x, int y) b = i < 8 ? (n - 1 - i, 8) : (8, n - 15 + i);
            if (m[a.y, a.x]) first |= 1 << i;
            if (m[b.y, b.x]) second |= 1 << i;
        }
        int best = -1, bestDistance = int.MaxValue;
        for (int data = 0; data < 32; data++)
            foreach (int word in new[] { first, second })
            {
                int d = int.PopCount(word ^ FormatWords[data]);
                if (d < bestDistance) { bestDistance = d; best = data; }
            }
        if (bestDistance > 3) return null;
        return (LevelFromBits[best >> 3], best & 7);
    }

    public static bool Masked(int mask, int x, int y) => mask switch
    {
        0 => (x + y) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (x + y) % 3 == 0,
        4 => (x / 3 + y / 2) % 2 == 0,
        5 => x * y % 2 + x * y % 3 == 0,
        6 => (x * y % 2 + x * y % 3) % 2 == 0,
        _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
    };

    /// <summary>The codewords in placement order, read up and down in two-column strips from the right.</summary>
    public static byte[] ReadCodewords(bool[,] m, int version, int mask)
    {
        int n = m.GetLength(0);
        var function = QrVersion.FunctionModules(version);
        int total = (n * n - Count(function)) / 8;
        var codewords = new byte[total];
        int bit = 0;
        for (int right = n - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            bool upward = ((right + 1) & 2) == 0;
            for (int v = 0; v < n; v++)
            {
                int y = upward ? n - 1 - v : v;
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    if (function[y, x]) continue;
                    if (bit >= total * 8) continue;
                    if (m[y, x] ^ Masked(mask, x, y)) codewords[bit >> 3] |= (byte)(0x80 >> (bit & 7));
                    bit++;
                }
            }
        }
        return codewords;
    }

    private static int Count(bool[,] f)
    {
        int c = 0;
        foreach (bool b in f) if (b) c++;
        return c;
    }

    /// <summary>Splits interleaved codewords into blocks and returns the data codewords in order.</summary>
    public static byte[]? Deinterleave(byte[] codewords, int version, QrErrorCorrection level, Func<byte[], int, bool> correct)
    {
        var layout = QrVersion.For(version, level);
        int blockCount = layout.BlockCount;
        var blocks = new byte[blockCount][];
        var dataLengths = new int[blockCount];
        for (int b = 0; b < blockCount; b++)
        {
            dataLengths[b] = b < layout.Count1 ? layout.Data1 : layout.Data2;
            blocks[b] = new byte[dataLengths[b] + layout.EcPerBlock];
        }
        int k = 0;
        int maxData = dataLengths.Max();
        for (int i = 0; i < maxData; i++)
            for (int b = 0; b < blockCount; b++)
                if (i < dataLengths[b]) blocks[b][i] = codewords[k++];
        for (int i = 0; i < layout.EcPerBlock; i++)
            for (int b = 0; b < blockCount; b++) blocks[b][dataLengths[b] + i] = codewords[k++];
        var data = new List<byte>(layout.DataCodewords);
        for (int b = 0; b < blockCount; b++)
        {
            if (!correct(blocks[b], layout.EcPerBlock)) return null;
            data.AddRange(blocks[b].AsSpan(0, dataLengths[b]).ToArray());
        }
        return data.ToArray();
    }
}
