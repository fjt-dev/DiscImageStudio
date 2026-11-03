using System.Numerics;

namespace DiscImageStudio.Core.Carrier;

/// <summary>
/// Combinatorial coding of one group of binary pattern symbols.
///
/// A drawn disc area is built from two payload byte values (for DVD the scrambled
/// bytes 0xA5 and 0x92). The picture only depends on how many "white" symbols a
/// neighbourhood contains, not on their exact order inside a few dozen micrometres.
/// A group of <see cref="Size"/> symbols with <c>k</c> white symbols can therefore
/// be arranged in C(16, k) ways without changing the picture, which lets the
/// arrangement carry floor(log2 C(16, k)) data bits. The decoder recovers k by
/// counting, so it needs neither the source image nor any side information.
///
/// Arrangements are ranked in the combinatorial number system (colex order):
/// a pattern with set positions p1 &lt; p2 &lt; … &lt; pk has rank Σ C(pi, i).
/// </summary>
public static class CombinadicGroup
{
    /// <summary>Symbols per group. Part of the on-disc format.</summary>
    public const int Size = 16;

    private static readonly uint[,] Binomial = BuildBinomials();

    private static readonly int[] CapacityByOnes = BuildCapacities();

    /// <summary>Number of data bits a group with <paramref name="ones"/> white symbols carries.</summary>
    public static int CapacityBits(int ones)
    {
        if ((uint)ones > Size)
            throw new ArgumentOutOfRangeException(nameof(ones));
        return CapacityByOnes[ones];
    }

    /// <summary>
    /// Same for a group of <paramref name="size"/> symbols (0..16). Media whose symbols that
    /// sit close together on the disc do not come in tidy runs of 16 use smaller groups.
    /// </summary>
    public static int CapacityBits(int size, int ones)
    {
        if ((uint)size > Size || (uint)ones > size)
            throw new ArgumentOutOfRangeException(nameof(ones));
        return BitOperations.Log2(Binomial[size, ones]);
    }

    /// <summary>The number of distinct arrangements C(16, ones).</summary>
    public static uint Arrangements(int ones)
    {
        if ((uint)ones > Size)
            throw new ArgumentOutOfRangeException(nameof(ones));
        return Binomial[Size, ones];
    }

    /// <summary>
    /// Returns the arrangement bit mask (bit j = symbol j is white) of rank
    /// <paramref name="rank"/> among the patterns with <paramref name="ones"/> white symbols.
    /// </summary>
    public static ushort Unrank(int ones, uint rank) => Unrank(Size, ones, rank);

    /// <summary>Unrank within a group of <paramref name="size"/> symbols.</summary>
    public static ushort Unrank(int size, int ones, uint rank)
    {
        if ((uint)size > Size || (uint)ones > size)
            throw new ArgumentOutOfRangeException(nameof(ones));
        if (rank >= Binomial[size, ones])
            throw new ArgumentOutOfRangeException(nameof(rank));
        uint mask = 0;
        uint remaining = rank;
        int position = size - 1;
        for (int index = ones; index >= 1; index--)
        {
            // Largest position p with C(p, index) <= remaining.
            while (Binomial[position, index] > remaining)
                position--;
            mask |= 1u << position;
            remaining -= Binomial[position, index];
            position--;
        }

        return (ushort)mask;
    }

    /// <summary>Inverse of <see cref="Unrank"/>.</summary>
    public static uint Rank(ushort mask)
    {
        uint rank = 0;
        int index = 0;
        uint bits = mask;
        while (bits != 0)
        {
            int position = BitOperations.TrailingZeroCount(bits);
            bits &= bits - 1;
            index++;
            rank += Binomial[position, index];
        }

        return rank;
    }

    private static uint[,] BuildBinomials()
    {
        uint[,] table = new uint[Size + 1, Size + 1];
        for (int n = 0; n <= Size; n++)
        {
            table[n, 0] = 1;
            for (int k = 1; k <= n; k++)
                table[n, k] = table[n - 1, k - 1] + (k <= n - 1 ? table[n - 1, k] : 0);
        }

        return table;
    }

    private static int[] BuildCapacities()
    {
        int[] capacities = new int[Size + 1];
        for (int ones = 0; ones <= Size; ones++)
            capacities[ones] = BitOperations.Log2(Binomial[Size, ones]);
        return capacities;
    }
}
