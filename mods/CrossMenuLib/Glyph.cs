namespace CrossMenuLib;

/// <summary>
/// Minimal 5×7 bitmap font, just enough to stamp a single uppercase letter (or '?'
/// / digit) into a placeholder icon. Not a real font — covers A–Z, 0–9, '?'.
/// </summary>
internal static class Glyph
{
    // Each glyph: 7 rows of 5 bits, MSB = leftmost column.
    private static readonly System.Collections.Generic.Dictionary<char, byte[]> Font = Build();

    /// <summary>True if the 5×7 glyph for <paramref name="ch"/> covers texel (x,y) of an NxN icon.</summary>
    internal static bool Covers(char ch, int x, int y, int n)
    {
        if (!Font.TryGetValue(ch, out var rows)) rows = Font['?'];
        // centre a 5×7 cell occupying ~50% of the icon
        float cellW = n * 0.42f, cellH = n * 0.50f;
        float ox = (n - cellW) * 0.5f, oy = (n - cellH) * 0.5f;
        int gx = (int)((x - ox) / cellW * 5f);
        int gy = (int)((y - oy) / cellH * 7f);
        if (gx < 0 || gx > 4 || gy < 0 || gy > 6) return false;
        // texture y is bottom-up; font rows are top-down
        int row = 6 - gy;
        return (rows[row] & (1 << (4 - gx))) != 0;
    }

    private static System.Collections.Generic.Dictionary<char, byte[]> Build()
    {
        var f = new System.Collections.Generic.Dictionary<char, byte[]>();
        void G(char c, params byte[] r) => f[c] = r;
        // 0b____ over 5 columns
        G('?', 0b01110, 0b10001, 0b00010, 0b00100, 0b00100, 0b00000, 0b00100);
        G('A', 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001);
        G('B', 0b11110, 0b10001, 0b11110, 0b10001, 0b10001, 0b10001, 0b11110);
        G('C', 0b01110, 0b10001, 0b10000, 0b10000, 0b10000, 0b10001, 0b01110);
        G('D', 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110);
        G('E', 0b11111, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000, 0b11111);
        G('F', 0b11111, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000, 0b10000);
        G('G', 0b01110, 0b10001, 0b10000, 0b10111, 0b10001, 0b10001, 0b01110);
        G('H', 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001, 0b10001);
        G('I', 0b01110, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        G('J', 0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100);
        G('K', 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001);
        G('L', 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111);
        G('M', 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001);
        G('N', 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001);
        G('O', 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        G('P', 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000);
        G('Q', 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101);
        G('R', 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001);
        G('S', 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110);
        G('T', 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        G('U', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110);
        G('V', 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100);
        G('W', 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b11011, 0b10001);
        G('X', 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b01010, 0b10001);
        G('Y', 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100);
        G('Z', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111);
        G('0', 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110);
        G('1', 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110);
        G('2', 0b01110, 0b10001, 0b00001, 0b00110, 0b01000, 0b10000, 0b11111);
        G('3', 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110);
        G('4', 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010);
        G('5', 0b11111, 0b10000, 0b11110, 0b00001, 0b00001, 0b10001, 0b01110);
        G('6', 0b01110, 0b10000, 0b11110, 0b10001, 0b10001, 0b10001, 0b01110);
        G('7', 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000);
        G('8', 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110);
        G('9', 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110);
        return f;
    }
}
