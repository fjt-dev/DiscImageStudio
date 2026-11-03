namespace DiscImageStudio.Core.Carrier;

/// <summary>CRC-32 (IEEE 802.3, reflected, as used by zip and PNG).</summary>
public static class CarrierCrc32
{
    private static readonly uint[] Table = BuildTable();

    public const uint Initial = 0xFFFF_FFFFu;

    public static uint Update(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
            state = Table[(state ^ value) & 0xFF] ^ (state >> 8);
        return state;
    }

    public static uint Finish(uint state) => ~state;

    public static uint Compute(ReadOnlySpan<byte> data) => Finish(Update(Initial, data));

    private static uint[] BuildTable()
    {
        uint[] table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            uint value = index;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB8_8320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }

        return table;
    }
}

/// <summary>
/// Stateless keystream used to whiten carrier bits. Without it, regular data such as
/// runs of zero bits would always select the lowest-ranked arrangement and stack the
/// white symbols at one end of every group.
/// </summary>
internal static class CarrierWhitening
{
    /// <summary>Keystream bit for an absolute stream bit position.</summary>
    internal static int Bit(ulong position, bool header)
    {
        ulong word = Mix((position >> 6) + (header ? 0x9E37_79B9_7F4A_7C15UL : 0));
        return (int)((word >> (63 - (int)(position & 63))) & 1);
    }

    // SplitMix64 finalizer.
    private static ulong Mix(ulong value)
    {
        value += 0x9E37_79B9_7F4A_7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }
}

/// <summary>MSB-first bit access over byte buffers.</summary>
internal static class BitBuffer
{
    internal static int Get(ReadOnlySpan<byte> buffer, int bit)
        => (buffer[bit >> 3] >> (7 - (bit & 7))) & 1;

    internal static void Set(Span<byte> buffer, int bit, int value)
    {
        int mask = 0x80 >> (bit & 7);
        if (value != 0)
            buffer[bit >> 3] |= (byte)mask;
        else
            buffer[bit >> 3] &= (byte)~mask;
    }
}
