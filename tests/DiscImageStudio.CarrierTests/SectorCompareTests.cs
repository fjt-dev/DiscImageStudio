using DiscImageStudio.Core.Carrier;

internal static class SectorCompareTests
{
    internal static void Run()
    {
        Random random = new(12);
        byte[] expected = new byte[40 * 2048];
        random.NextBytes(expected);
        SectorCompareResult same = SectorImageComparer.Compare(new MemoryStream(expected), new MemoryStream(expected));
        Check.True(same.AllIdentical && same.Identical == 40, "identical images compare equal");

        byte[] actual = (byte[])expected.Clone();
        actual[5 * 2048 + 17] ^= 0x40;                    // one wrong byte
        Array.Clear(actual, 20 * 2048, 2048);             // one unread sector
        SectorCompareResult result = SectorImageComparer.Compare(new MemoryStream(expected), new MemoryStream(actual));
        Check.Equal(2L, result.DifferentSectors, "two sectors differ");
        Check.Equal(1L, result.ZeroInActual, "one of them is all zero (unread)");
        SectorDifference first = result.Differences[0];
        Check.True(first.Lba == 5 && first.DifferingBytes == 1 && first.FirstOffset == 17
            && first.Actual == (byte)(first.Expected ^ 0x40), "the single wrong byte is located");

        SectorCompareResult shorter = SectorImageComparer.Compare(
            new MemoryStream(expected), new MemoryStream(expected, 0, 30 * 2048));
        Check.True(!shorter.AllIdentical && shorter.Compared == 30, "a short read-back is not a pass");
        SectorCompareResult longer = SectorImageComparer.Compare(
            new MemoryStream(expected), new MemoryStream([.. expected, .. new byte[5 * 2048]]));
        Check.True(longer.AllIdentical, "a longer read-back (lead-out padding) still passes");
        SectorCompareResult ranged = SectorImageComparer.Compare(
            new MemoryStream(expected), new MemoryStream(actual), fromLba: 6, toLbaExclusive: 20);
        Check.True(ranged.AllIdentical == false || ranged.DifferentSectors == 0, "a range can be limited");
        Check.Equal(0L, ranged.DifferentSectors, "the limited range excludes both differences");
        Console.WriteLine("sector-compare: passed (identical, wrong byte located, unread sector, short and long read-backs, ranges)");
    }
}
