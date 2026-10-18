using System.IO;
using System.Text;

namespace KillerTools.Qr;

/// <summary>Decodes the data codewords: numeric, alphanumeric, byte (with ECI) and kanji segments.</summary>
internal static class Segments
{
    private const string Alphanumeric = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    static Segments() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string? Decode(byte[] data, int version)
    {
        var bits = new BitReader(data);
        var text = new StringBuilder();
        var pendingBytes = new List<byte>();
        Encoding? eci = null;
        int sizeClass = version <= 9 ? 0 : version <= 26 ? 1 : 2;

        void FlushBytes()
        {
            if (pendingBytes.Count == 0) return;
            var raw = pendingBytes.ToArray();
            text.Append((eci ?? Guess(raw)).GetString(raw));
            pendingBytes.Clear();
        }

        while (bits.Available >= 4)
        {
            int mode = bits.Read(4);
            if (mode == 0) break;                       // terminator
            switch (mode)
            {
                case 0b0001:                            // numeric
                {
                    FlushBytes();
                    int count = bits.Read(new[] { 10, 12, 14 }[sizeClass]);
                    while (count >= 3) { if (!Digits(bits, 10, 3, text)) return null; count -= 3; }
                    if (count == 2 && !Digits(bits, 7, 2, text)) return null;
                    if (count == 1 && !Digits(bits, 4, 1, text)) return null;
                    break;
                }
                case 0b0010:                            // alphanumeric
                {
                    FlushBytes();
                    int count = bits.Read(new[] { 9, 11, 13 }[sizeClass]);
                    while (count >= 2)
                    {
                        if (bits.Available < 11) return null;
                        int v = bits.Read(11);
                        if (v / 45 >= 45) return null;
                        text.Append(Alphanumeric[v / 45]).Append(Alphanumeric[v % 45]);
                        count -= 2;
                    }
                    if (count == 1)
                    {
                        if (bits.Available < 6) return null;
                        int v = bits.Read(6);
                        if (v >= 45) return null;
                        text.Append(Alphanumeric[v]);
                    }
                    break;
                }
                case 0b0100:                            // byte
                {
                    int count = bits.Read(new[] { 8, 16, 16 }[sizeClass]);
                    if (bits.Available < count * 8) return null;
                    for (int i = 0; i < count; i++) pendingBytes.Add((byte)bits.Read(8));
                    break;
                }
                case 0b1000:                            // kanji, 13 bits a character in Shift_JIS
                {
                    FlushBytes();
                    int count = bits.Read(new[] { 8, 10, 12 }[sizeClass]);
                    if (bits.Available < count * 13) return null;
                    var sjis = new byte[count * 2];
                    for (int i = 0; i < count; i++)
                    {
                        int v = bits.Read(13);
                        int assembled = (v / 0xC0) << 8 | (v % 0xC0);
                        assembled += assembled < 0x1F00 ? 0x8140 : 0xC140;
                        sjis[2 * i] = (byte)(assembled >> 8);
                        sjis[2 * i + 1] = (byte)assembled;
                    }
                    text.Append(Encoding.GetEncoding(932).GetString(sjis));
                    break;
                }
                case 0b0111:                            // ECI designator
                {
                    FlushBytes();
                    int first = bits.Read(8);
                    int value = (first & 0x80) == 0 ? first
                        : (first & 0xC0) == 0x80 ? ((first & 0x3F) << 8) | bits.Read(8)
                        : ((first & 0x1F) << 16) | bits.Read(16);
                    eci = FromEci(value);
                    break;
                }
                case 0b0011:                            // structured append: sequence and parity, then the data
                    bits.Read(16);
                    break;
                case 0b0101:                            // FNC1 in the first position
                    break;
                case 0b1001:                            // FNC1 in the second position
                    bits.Read(8);
                    break;
                default:
                    return null;
            }
        }
        FlushBytes();
        return text.ToString();
    }

    private static bool Digits(BitReader bits, int width, int digits, StringBuilder text)
    {
        if (bits.Available < width) return false;
        int v = bits.Read(width);
        if (v >= (digits == 3 ? 1000 : digits == 2 ? 100 : 10)) return false;
        text.Append(v.ToString(digits == 3 ? "000" : digits == 2 ? "00" : "0", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>With no ECI, bytes that are valid UTF-8 read as UTF-8, otherwise as ISO-8859-1 (the standard's default).</summary>
    private static Encoding Guess(byte[] raw)
    {
        try
        {
            new UTF8Encoding(false, true).GetString(raw);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException) { return Encoding.Latin1; }
    }

    private static Encoding? FromEci(int value)
    {
        try { return EciEncoding(value); }
        catch (ArgumentException) { return null; }      // a character set .NET does not carry
        catch (NotSupportedException) { return null; }
    }

    private static Encoding? EciEncoding(int value) => value switch
    {
        0 or 2 => Encoding.GetEncoding(437),
        1 or 3 => Encoding.Latin1,
        >= 4 and <= 18 when value != 14 => Encoding.GetEncoding("iso-8859-" + (value - 2)),
        20 => Encoding.GetEncoding(932),
        21 => Encoding.GetEncoding(1250),
        22 => Encoding.GetEncoding(1251),
        23 => Encoding.GetEncoding(1252),
        24 => Encoding.GetEncoding(1256),
        25 => Encoding.BigEndianUnicode,
        26 => Encoding.UTF8,
        27 => Encoding.ASCII,
        28 => Encoding.GetEncoding(950),
        29 => Encoding.GetEncoding(936),
        30 => Encoding.GetEncoding(949),
        _ => null,
    };

    private sealed class BitReader(byte[] data)
    {
        private int _position;
        public int Available => data.Length * 8 - _position;

        public int Read(int count)
        {
            if (count > Available) throw new InvalidDataException("The code ends inside a segment.");
            int v = 0;
            for (int i = 0; i < count; i++, _position++)
                v = (v << 1) | ((data[_position >> 3] >> (7 - (_position & 7))) & 1);
            return v;
        }
    }
}
