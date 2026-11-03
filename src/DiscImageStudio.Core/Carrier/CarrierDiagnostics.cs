using System.Security.Cryptography;
using System.Text;

namespace DiscImageStudio.Core.Carrier;

/// <summary>A run of sectors, <see cref="Start"/> inclusive, <see cref="EndExclusive"/> exclusive.</summary>
public readonly record struct LbaRange(long Start, long EndExclusive)
{
    public long Length => EndExclusive - Start;

    public override string ToString() => Length == 1 ? $"{Start}" : $"{Start}-{EndExclusive - 1}";
}

/// <summary>
/// What a scan of a read-back image found beyond "the file did or did not come back": where the
/// frames that should have carried data are missing and which sectors look unread. Meant for
/// telling a drive that returned wrong bytes from one that could not read at all.
/// </summary>
public sealed class CarrierScanReport
{
    private const int MaxRuns = 10_000;
    private readonly List<byte> _accepted = [];
    private readonly List<int> _capacity = [];
    private readonly List<double> _valid = [];
    private readonly List<LbaRange> _zeroSectors = [];
    private long _zeroSectorCount;

    public long FramesScanned => _accepted.Count;

    public int FramesAccepted { get; private set; }

    /// <summary>First and last sector of the span in which frames were accepted (null when none).</summary>
    public LbaRange? AcceptedSpan { get; private set; }

    /// <summary>
    /// Frames inside the accepted span that did not decode although they should have: their
    /// arrangement could have carried data, or they hold mostly bytes that are no pattern data at
    /// all (unread or corrupted). Transparent parts of a picture look like the latter and are not errors.
    /// </summary>
    public IReadOnlyList<LbaRange> SuspectFrames { get; private set; } = [];

    public long SuspectFrameCount { get; private set; }

    /// <summary>All-zero sectors inside the accepted span. A drawing never has them, so they are unread sectors.</summary>
    public IReadOnlyList<LbaRange> UnreadSectors { get; private set; } = [];

    public long UnreadSectorCount { get; private set; }

    /// <summary>
    /// Records one frame, in order: whether it decoded, how many raw bits its arrangement holds
    /// and what share of its groups held only valid pattern symbols.
    /// </summary>
    public void AddFrame(bool accepted, int capacityBits, double validGroupFraction)
    {
        _accepted.Add(accepted ? (byte)1 : (byte)0);
        _capacity.Add(capacityBits);
        _valid.Add(validGroupFraction);
        if (accepted)
            FramesAccepted++;
    }

    /// <summary>Records an all-zero sector (call in ascending order).</summary>
    public void AddZeroSector(long lba)
    {
        _zeroSectorCount++;
        if (_zeroSectors.Count > 0 && _zeroSectors[^1].EndExclusive == lba)
            _zeroSectors[^1] = _zeroSectors[^1] with { EndExclusive = lba + 1 };
        else if (_zeroSectors.Count < MaxRuns)
            _zeroSectors.Add(new LbaRange(lba, lba + 1));
    }

    /// <summary>Works out the findings. <paramref name="sectorsOfFrame"/> maps a frame index to its sectors.</summary>
    public void Complete(Func<long, LbaRange> sectorsOfFrame)
    {
        int first = _accepted.IndexOf(1);
        int last = _accepted.LastIndexOf(1);
        if (first < 0)
            return;
        AcceptedSpan = new LbaRange(sectorsOfFrame(first).Start, sectorsOfFrame(last).EndExclusive);
        List<LbaRange> suspect = [];
        for (int frame = first; frame <= last; frame++)
        {
            // Fine: it decoded, or it is simply flat (valid but nothing to carry). Not fine: it could
            // have carried data yet did not decode, or it is mostly not pattern data at all.
            bool flat = CarrierBlockFormat.UsableDataBits(_capacity[frame]) == 0 && _valid[frame] >= 0.5;
            if (_accepted[frame] == 1 || flat)
                continue;
            SuspectFrameCount++;
            LbaRange range = sectorsOfFrame(frame);
            if (suspect.Count > 0 && suspect[^1].EndExclusive >= range.Start)
                suspect[^1] = suspect[^1] with { EndExclusive = Math.Max(suspect[^1].EndExclusive, range.EndExclusive) };
            else if (suspect.Count < MaxRuns)
                suspect.Add(range);
        }

        SuspectFrames = suspect;
        List<LbaRange> unread = [];
        foreach (LbaRange run in _zeroSectors)
        {
            long start = Math.Max(run.Start, AcceptedSpan.Value.Start);
            long end = Math.Min(run.EndExclusive, AcceptedSpan.Value.EndExclusive);
            if (start >= end)
                continue;
            UnreadSectorCount += end - start;
            unread.Add(new LbaRange(start, end));
        }

        UnreadSectors = unread;
    }

    /// <summary>A short, human readable account of the findings.</summary>
    public string Describe(int maxRuns = 12)
    {
        StringBuilder text = new();
        text.Append($"frames scanned: {FramesScanned}, decoded: {FramesAccepted}");
        if (AcceptedSpan is { } span)
            text.Append($"; data found in LBA {span}");
        else
            text.Append("; no frame decoded");
        text.AppendLine();
        text.Append(SuspectFrameCount == 0
            ? "frames that should carry data but do not: none"
            : $"frames that should carry data but do not: {SuspectFrameCount} (LBA {Runs(SuspectFrames, maxRuns)})");
        text.AppendLine();
        text.Append(UnreadSectorCount == 0
            ? "all-zero (unread) sectors inside the drawing: none"
            : $"all-zero (unread) sectors inside the drawing: {UnreadSectorCount} (LBA {Runs(UnreadSectors, maxRuns)})");
        return text.ToString();
    }

    internal static string Runs(IReadOnlyList<LbaRange> runs, int max)
        => string.Join(", ", runs.Take(max)) + (runs.Count > max ? $", … ({runs.Count - max} more runs)" : string.Empty);

    /// <summary>SHA-256 of a stream from its start, as lower-case hex.</summary>
    public static string Sha256Hex(Stream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>Outcome of <see cref="SectorImageComparer.Compare"/>.</summary>
public sealed record SectorCompareResult(
    long ExpectedSectors,
    long ActualSectors,
    long Compared,
    long Identical,
    IReadOnlyList<SectorDifference> Differences,
    long DifferentSectors,
    long ZeroInActual)
{
    public bool AllIdentical => DifferentSectors == 0 && ActualSectors >= ExpectedSectors;
}

/// <summary>One differing sector: how many bytes differ, and the first of them.</summary>
public sealed record SectorDifference(long Lba, int DifferingBytes, int FirstOffset, byte Expected, byte Actual);

/// <summary>
/// Compares the image that was generated with the image read back from the disc, sector by
/// sector. The first thing to check on real hardware: did the drive return the bytes that were written?
/// </summary>
public static class SectorImageComparer
{
    public static SectorCompareResult Compare(
        Stream expected, Stream actual, int sectorBytes = 2048,
        long fromLba = 0, long toLbaExclusive = long.MaxValue, int keepDetails = 20)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        long expectedSectors = expected.Length / sectorBytes;
        long actualSectors = actual.Length / sectorBytes;
        long end = Math.Min(Math.Min(expectedSectors, actualSectors), toLbaExclusive);
        byte[] a = new byte[sectorBytes], b = new byte[sectorBytes];
        long identical = 0, different = 0, zero = 0, compared = 0;
        List<SectorDifference> details = [];
        expected.Position = fromLba * sectorBytes;
        actual.Position = fromLba * sectorBytes;
        for (long lba = fromLba; lba < end; lba++)
        {
            expected.ReadExactly(a);
            actual.ReadExactly(b);
            compared++;
            if (a.AsSpan().SequenceEqual(b))
            {
                identical++;
                continue;
            }

            different++;
            if (b.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                zero++;
            if (details.Count < keepDetails)
            {
                int count = 0, first = -1;
                for (int index = 0; index < sectorBytes; index++)
                {
                    if (a[index] == b[index])
                        continue;
                    count++;
                    if (first < 0)
                        first = index;
                }

                details.Add(new SectorDifference(lba, count, first, a[first], b[first]));
            }
        }

        return new SectorCompareResult(expectedSectors, actualSectors, compared, identical, details, different, zero);
    }
}
