using DiscImageStudio.Core.Carrier;
using DvdImageSolver.Encoding;

namespace DvdImageSolver.Solver;

/// <summary>
/// Hook that sees every generated payload block, in ascending LBA order and exactly once,
/// just before it is written. It may rewrite the payload bytes.
/// </summary>
public interface IDvdBlockPayloadProcessor
{
    void Process(uint lba, uint psnOffset, Span<byte> payloads);
}

/// <summary>Running totals of an embedding pass.</summary>
public sealed record DvdBitCarrierStats(
    long BlocksProcessed,
    long BlocksCarrying,
    long CapacityBits,
    ulong StreamBitsCarried,
    ulong StreamBitsTotal)
{
    public bool Complete => StreamBitsCarried >= StreamBitsTotal;

    /// <summary>User-visible capacity in bytes, minus the per-block frame headers.</summary>
    public long CapacityBytes => Math.Max(0, CapacityBits
        - (BlocksProcessed * CarrierBlockFormat.HeaderBits)) / 8;
}

/// <summary>
/// Stores a byte stream in the arrangement of the drawn area ("one bit per byte" channel).
///
/// The picture is made of payload bytes whose scrambled value is either 0xA5 (dark) or
/// 0x92 (light). The generator decides how many light bytes a neighbourhood has from the
/// image; this processor keeps that count for every group of 16 consecutive bytes and only
/// changes <em>which</em> of the 16 are light. Each group carries floor(log2 C(16, k)) bits,
/// about 0.7–0.8 bit per byte in midtones and nothing in flat black or white.
/// Reading needs no image: see <see cref="DvdBitCarrierExtractor"/>.
/// </summary>
public sealed class DvdBitCarrierEmbedder : IDvdBlockPayloadProcessor
{
    private readonly CarrierBlockEncoder _encoder;
    private readonly ICarrierBitSource _source;
    private readonly byte[] _symbols = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
    private long _blocksProcessed;
    private long _blocksCarrying;
    private long _capacityBits;

    public DvdBitCarrierEmbedder(ICarrierBitSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _encoder = new CarrierBlockEncoder(source);
    }

    public DvdBitCarrierStats Stats => new(
        _blocksProcessed, _blocksCarrying, _capacityBits, _encoder.NextOffset, _source.TotalBits);

    public void Process(uint lba, uint psnOffset, Span<byte> payloads)
    {
        if (payloads.Length != DvdEccBlockEncoder.PayloadBytesPerBlock)
            throw new ArgumentException("A DVD ECC block holds 32768 payload bytes.", nameof(payloads));
        uint firstPsn = checked(lba + psnOffset);
        DvdBitCarrier.BuildSymbols(payloads, firstPsn, _symbols);
        CarrierBlockInfo info = _encoder.Encode(_symbols);
        _blocksProcessed++;
        _capacityBits += info.CapacityBits;
        if (!info.Used)
            return;
        _blocksCarrying++;
        DvdBitCarrier.ApplySymbols(_symbols, firstPsn, payloads);
    }
}

public sealed record DvdBitCarrierExtractProgress(long BlocksScanned, long TotalBlocks, int BlocksAccepted);

public static class DvdBitCarrierExtractor
{
    /// <summary>
    /// Scans a raw 2048-byte-sector image (an ISO file or a drive device) for carrier
    /// frames and reassembles the embedded stream into <paramref name="output"/>.
    /// </summary>
    /// <param name="iso">Sector image, positioned at LBA 0 of the disc.</param>
    /// <param name="output">Readable, writable, seekable destination for the user data.</param>
    /// <param name="psnOffset">PSN of LBA 0; 0x30000 on a standard disc.</param>
    public static CarrierExtractResult Extract(
        Stream iso,
        Stream output,
        uint psnOffset = 0x30000,
        Action<DvdBitCarrierExtractProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(output);
        if (!iso.CanRead || !iso.CanSeek)
            throw new ArgumentException("The sector image must be readable and seekable.", nameof(iso));

        long totalSectors = iso.Length / DvdEccBlockEncoder.PayloadBytesPerSector;
        uint firstLba = (16 - (psnOffset & 0xF)) & 0xF;
        long totalBlocks = Math.Max(0, (totalSectors - firstLba) / DvdEccBlockEncoder.SectorCount);
        int parallelism = Math.Max(1, Environment.ProcessorCount);
        int batchSize = parallelism * 2;
        byte[][] raw = new byte[batchSize][];
        for (int index = 0; index < batchSize; index++)
            raw[index] = new byte[DvdEccBlockEncoder.PayloadBytesPerBlock];
        CarrierBlock?[] decoded = new CarrierBlock?[batchSize];
        CarrierAssembler assembler = new(output);
        ParallelOptions options = new()
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = cancellationToken,
        };

        iso.Position = (long)firstLba * DvdEccBlockEncoder.PayloadBytesPerSector;
        long scanned = 0;
        using ThreadLocal<(CarrierBlockDecoder Decoder, byte[] Symbols)> workers = new(
            () => (new CarrierBlockDecoder(), new byte[DvdEccBlockEncoder.PayloadBytesPerBlock]));
        while (scanned < totalBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(batchSize, totalBlocks - scanned);
            for (int index = 0; index < count; index++)
                iso.ReadExactly(raw[index]);
            long batchStart = scanned;
            Parallel.For(0, count, options, index =>
            {
                (CarrierBlockDecoder decoder, byte[] symbols) = workers.Value;
                uint lba = checked((uint)(firstLba + ((batchStart + index) * DvdEccBlockEncoder.SectorCount)));
                DvdBitCarrier.BuildSymbols(raw[index], checked(lba + psnOffset), symbols);
                decoded[index] = decoder.TryDecode(symbols, out CarrierBlock? block) ? block : null;
            });
            for (int index = 0; index < count; index++)
            {
                if (decoded[index] is { } block)
                    assembler.Add(block);
            }

            scanned += count;
            progress?.Invoke(new DvdBitCarrierExtractProgress(scanned, totalBlocks, assembler.BlocksAccepted));
        }

        return assembler.Finish();
    }
}

internal static class DvdBitCarrier
{
    /// <summary>
    /// Maps host payload bytes to pattern symbols: 0 for the dark byte, 1 for the light
    /// byte, 2 for anything else (filesystem data, uncontrolled bytes).
    /// </summary>
    internal static void BuildSymbols(ReadOnlySpan<byte> payloads, uint firstPsn, Span<byte> symbols)
    {
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(
                firstPsn + (uint)sector);
            int offset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            for (int index = 0; index < DvdEccBlockEncoder.PayloadBytesPerSector; index++)
            {
                byte scrambled = (byte)(payloads[offset + index] ^ mask[index]);
                symbols[offset + index] = scrambled == DvdGrayscalePalette.WhiteScrambledByte
                    ? (byte)1
                    : scrambled == DvdGrayscalePalette.BlackScrambledByte
                        ? (byte)0
                        : CarrierBlockFormat.InvalidSymbol;
            }
        }
    }

    /// <summary>Writes the valid symbols back as host bytes; invalid ones are left alone.</summary>
    internal static void ApplySymbols(ReadOnlySpan<byte> symbols, uint firstPsn, Span<byte> payloads)
    {
        for (int sector = 0; sector < DvdEccBlockEncoder.SectorCount; sector++)
        {
            ReadOnlySpan<byte> mask = DvdDataFrameBuilder.PayloadScramblerMask(
                firstPsn + (uint)sector);
            int offset = sector * DvdEccBlockEncoder.PayloadBytesPerSector;
            for (int index = 0; index < DvdEccBlockEncoder.PayloadBytesPerSector; index++)
            {
                byte symbol = symbols[offset + index];
                if (symbol > 1)
                    continue;
                byte scrambled = symbol == 1
                    ? DvdGrayscalePalette.WhiteScrambledByte
                    : DvdGrayscalePalette.BlackScrambledByte;
                payloads[offset + index] = (byte)(scrambled ^ mask[index]);
            }
        }
    }
}
