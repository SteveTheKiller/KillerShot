namespace KillerTools.Qr;

/// <summary>Reed-Solomon error correction over GF(256) with the QR polynomial x^8+x^4+x^3+x^2+1 and
/// generator roots a^0 .. a^(n-1): syndromes, Berlekamp-Massey, Chien search and Forney.</summary>
internal static class ReedSolomon
{
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static ReedSolomon()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    private static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[Log[a] + 255 - Log[b]];
    private static byte Pow(int power) => Exp[((power % 255) + 255) % 255];

    /// <summary>Corrects a block (data then error correction codewords, highest degree first) in place.
    /// Returns false when the block holds more errors than its codewords can fix.</summary>
    public static bool Correct(byte[] block, int ecCount)
    {
        int n = block.Length;
        var syndromes = new byte[ecCount];
        bool clean = true;
        for (int i = 0; i < ecCount; i++)
        {
            byte s = 0, root = Pow(i);
            foreach (byte c in block) s = (byte)(Mul(s, root) ^ c);
            syndromes[i] = s;
            if (s != 0) clean = false;
        }
        if (clean) return true;

        // Berlekamp-Massey: the error locator, lowest degree first.
        var locator = new byte[ecCount + 1]; locator[0] = 1;
        var previous = new byte[ecCount + 1]; previous[0] = 1;
        int errors = 0, shift = 1;
        byte lastDiscrepancy = 1;
        for (int r = 0; r < ecCount; r++)
        {
            byte d = syndromes[r];
            for (int i = 1; i <= errors; i++) d ^= Mul(locator[i], syndromes[r - i]);
            if (d == 0) { shift++; continue; }
            var copy = (byte[])locator.Clone();
            byte factor = Div(d, lastDiscrepancy);
            for (int i = 0; i + shift <= ecCount; i++) locator[i + shift] ^= Mul(factor, previous[i]);
            if (2 * errors <= r)
            {
                errors = r + 1 - errors;
                previous = copy;
                lastDiscrepancy = d;
                shift = 1;
            }
            else shift++;
        }
        if (errors == 0 || 2 * errors > ecCount) return false;

        // Chien search: position p (counted from the last codeword) is in error when locator(a^-p) = 0.
        var positions = new List<int>();
        for (int p = 0; p < n; p++)
        {
            byte sum = 0, inverse = Pow(-p);
            byte power = 1;
            for (int i = 0; i <= errors; i++)
            {
                sum ^= Mul(locator[i], power);
                power = Mul(power, inverse);
            }
            if (sum == 0) positions.Add(p);
        }
        if (positions.Count != errors) return false;

        // Forney: error evaluator omega = (S * locator) mod x^ecCount, S(x) = sum syndromes[i] x^i.
        var omega = new byte[ecCount];
        for (int i = 0; i < ecCount; i++)
            for (int j = 0; j <= errors && j <= i; j++) omega[i] ^= Mul(syndromes[i - j], locator[j]);
        foreach (int p in positions)
        {
            byte xInverse = Pow(-p), x = Pow(p);
            byte num = 0, power = 1;
            for (int i = 0; i < ecCount; i++) { num ^= Mul(omega[i], power); power = Mul(power, xInverse); }
            // Formal derivative of the locator keeps only the odd terms.
            byte den = 0;
            power = 1;
            byte xInverseSquared = Mul(xInverse, xInverse);
            for (int i = 1; i <= errors; i += 2) { den ^= Mul(locator[i], power); power = Mul(power, xInverseSquared); }
            if (den == 0) return false;
            byte magnitude = Mul(x, Div(num, den));
            block[n - 1 - p] ^= magnitude;
        }
        // Confirm the result is a codeword.
        for (int i = 0; i < ecCount; i++)
        {
            byte s = 0, root = Pow(i);
            foreach (byte c in block) s = (byte)(Mul(s, root) ^ c);
            if (s != 0) return false;
        }
        return true;
    }
}
