using DiscImageStudio.Core.Carrier;

namespace DiscImageStudio.Cli;

/// <summary>
/// <c>compare</c>: the first check on real hardware. Compares the image that was generated with
/// the image read back from the disc and says which sectors differ, and whether they look unread.
/// </summary>
internal static class ImageCompareCommand
{
    internal const string Name = "compare";

    internal static void PrintHelp()
    {
        Console.WriteLine("\nTools");
        Console.WriteLine($"  {Name,-20} Compare a generated image with the image read back from a disc, sector by sector");
        Console.WriteLine("                       compare --expected generated.iso --actual readback.iso [--from-lba N] [--to-lba N] [--sector-bytes 2048]");
    }

    internal static int Run(string[] arguments)
    {
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
                throw new ArgumentException($"Unexpected or incomplete argument '{argument}'.");
            options[argument[2..]] = arguments[++index];
        }

        string expectedPath = options.TryGetValue("expected", out string? e) ? e : throw new ArgumentException("Missing --expected.");
        string actualPath = options.TryGetValue("actual", out string? a) ? a : throw new ArgumentException("Missing --actual.");
        int sectorBytes = options.TryGetValue("sector-bytes", out string? s) ? int.Parse(s) : 2048;
        long from = options.TryGetValue("from-lba", out string? f) ? long.Parse(f) : 0;
        long to = options.TryGetValue("to-lba", out string? t) ? long.Parse(t) : long.MaxValue;
        using FileStream expected = new(expectedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        using FileStream actual = new(actualPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        SectorCompareResult result = SectorImageComparer.Compare(expected, actual, sectorBytes, from, to);
        Console.WriteLine($"sectors: expected {result.ExpectedSectors}, read back {result.ActualSectors}, compared {result.Compared}");
        Console.WriteLine($"identical: {result.Identical} ({(result.Compared == 0 ? 0 : 100.0 * result.Identical / result.Compared):F4}%)");
        if (result.ActualSectors < result.ExpectedSectors && to == long.MaxValue)
            Console.WriteLine($"note: the read-back image is {result.ExpectedSectors - result.ActualSectors} sectors shorter than the generated one");
        if (result.DifferentSectors == 0)
        {
            Console.WriteLine("no differing sectors");
            return result.AllIdentical ? 0 : 1;
        }

        Console.WriteLine($"different: {result.DifferentSectors} sectors, of which {result.ZeroInActual} are all zero in the read-back (unread)");
        foreach (SectorDifference difference in result.Differences)
        {
            Console.WriteLine($"  LBA {difference.Lba}: {difference.DifferingBytes} bytes differ, first at offset {difference.FirstOffset} "
                + $"(expected 0x{difference.Expected:X2}, read 0x{difference.Actual:X2})");
        }

        if (result.DifferentSectors > result.Differences.Count)
            Console.WriteLine($"  … and {result.DifferentSectors - result.Differences.Count} more");
        return 1;
    }
}
