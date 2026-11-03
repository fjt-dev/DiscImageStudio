using System.Numerics;
using DiscImageStudio.Core.Carrier;

internal static class CombinadicGroupTests
{
    internal static void Run()
    {
        int[] expectedCapacity = [0, 4, 6, 9, 10, 12, 12, 13, 13, 13, 12, 12, 10, 9, 6, 4, 0];
        for (int ones = 0; ones <= CombinadicGroup.Size; ones++)
        {
            Check.Equal(expectedCapacity[ones], CombinadicGroup.CapacityBits(ones), $"capacity of k={ones}");
            Check.True(1u << CombinadicGroup.CapacityBits(ones) <= CombinadicGroup.Arrangements(ones),
                $"2^capacity must not exceed the arrangement count for k={ones}");
        }

        Check.Equal(12870u, CombinadicGroup.Arrangements(8), "C(16,8)");

        // Every 16-bit pattern ranks to a unique value below C(16,k) and unranks to itself.
        HashSet<(int, uint)> seen = [];
        for (int value = 0; value <= ushort.MaxValue; value++)
        {
            ushort mask = (ushort)value;
            int ones = BitOperations.PopCount(mask);
            uint rank = CombinadicGroup.Rank(mask);
            Check.True(rank < CombinadicGroup.Arrangements(ones), $"rank range for {mask:X4}");
            Check.True(seen.Add((ones, rank)), $"rank uniqueness for {mask:X4}");
            Check.Equal(mask, CombinadicGroup.Unrank(ones, rank), $"unrank(rank({mask:X4}))");
        }

        Console.WriteLine("combinadic-group: passed (capacity table, bijection over all 65536 patterns)");
    }
}
