using System.IO;
using DiscImageStudio.Core.Carrier;

namespace DiscImageStudio.Cd;

/// <summary>
/// Stores a byte stream in the arrangement of a CD data disc's drawing ("one bit per byte").
///
/// The picture is made of payload bytes whose scrambled value is 0x0D (dark) or 0xAA
/// (light). Neighbouring bytes of the track, as <see cref="CdCarrierGeometry"/> orders them,
/// may swap places without changing the picture: only how many light bytes a neighbourhood has
/// matters. This keeps that count for every group and lets the arrangement carry
/// floor(log2 C(n, k)) bits. Reading needs no image: see <see cref="CdBitCarrierExtractor"/>.
///
/// Use it with <see cref="CdDataImageGenerator"/>; it filters the finished sectors, so the
/// image sampling is untouched. A frame carries data only when all the sectors it reads belong
/// to the drawing, so the filesystem is never rearranged.
/// </summary>
public sealed class CdBitCarrier
{
    private readonly ICarrierBitSource _source;
    private CdBitCarrierStream? _stream;

    public CdBitCarrier(ICarrierBitSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public CarrierStats Stats => _stream?.Stats
        ?? new CarrierStats(0, 0, 0, 0, 0, _source.TotalBits);

    internal CdBitCarrierStream Wrap(Stream inner, long drawingStart, long drawingEndExclusive)
    {
        if (_stream is not null)
            throw new InvalidOperationException("A carrier embeds into one image only.");
        _stream = new CdBitCarrierStream(inner, _source, drawingStart, drawingEndExclusive);
        return _stream;
    }
}

/// <summary>
/// Passes 2048-byte sectors through, holding back as many as the frame that still needs
/// them (up to 18 ahead), rearranging each eligible frame as soon as its last sector is in.
/// </summary>
internal sealed class CdBitCarrierStream : Stream
{
    private const int SectorBytes = CdCarrierGeometry.SectorPayloadBytes;

    private readonly Stream _inner;
    private readonly CarrierBlockEncoder _encoder;
    private readonly ulong _totalBits;
    private readonly long _drawingStart;
    private readonly long _drawingEnd;
    private readonly Dictionary<long, byte[]> _sectors = [];
    private readonly Stack<byte[]> _spare = new();
    private readonly byte[] _symbols = new byte[CdCarrierGeometry.SlotsPerFrame];
    private readonly byte[][] _window = new byte[CdCarrierGeometry.SectorSpan][];
    private byte[] _current = new byte[SectorBytes];
    private int _fill;
    private long _received;
    private long _written;
    private long _nextFrame;
    private long _endFrame;
    private long _blocks, _carrying, _capacityBits, _usableBits;

    internal CdBitCarrierStream(Stream inner, ICarrierBitSource source, long drawingStart, long drawingEndExclusive)
    {
        _inner = inner;
        _encoder = new CarrierBlockEncoder(source);
        _totalBits = source.TotalBits;
        _drawingStart = drawingStart;
        _drawingEnd = drawingEndExclusive;
        _nextFrame = CdCarrierGeometry.FirstEligibleFrame(drawingStart);
        _endFrame = CdCarrierGeometry.EndEligibleFrame(drawingStart, drawingEndExclusive);
    }

    internal CarrierStats Stats => new(
        _blocks, _carrying, _capacityBits, _usableBits, _encoder.NextOffset, _totalBits);

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => (_received * SectorBytes) + _fill; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>Flushes what has been written; sectors a frame still needs stay held until <see cref="Complete"/>.</summary>
    public override void Flush() => _inner.Flush();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int take = Math.Min(buffer.Length, SectorBytes - _fill);
            buffer[..take].CopyTo(_current.AsSpan(_fill));
            _fill += take;
            buffer = buffer[take..];
            if (_fill < SectorBytes)
                continue;
            _sectors[_received++] = _current;
            _current = _spare.Count > 0 ? _spare.Pop() : new byte[SectorBytes];
            _fill = 0;
            Advance();
        }
    }

    /// <summary>Call after the last sector: processes what is left and writes every held sector.</summary>
    internal void Complete()
    {
        if (_fill != 0)
            throw new InvalidOperationException("The image does not end on a sector boundary.");
        // Every sector that will exist is in. A frame is processed the moment its last sector
        // arrives, so any frame still waiting lies beyond the end of the image.
        _endFrame = _nextFrame;
        FlushFinished();
        if (_written != _received)
            throw new InvalidOperationException("Sectors were left unwritten.");
        _inner.Flush();
    }

    private void Advance()
    {
        while (_nextFrame < _endFrame
               && CdCarrierGeometry.FirstSector(_nextFrame) + CdCarrierGeometry.SectorSpan <= _received)
        {
            ProcessFrame(_nextFrame);
            _nextFrame++;
        }

        FlushFinished();
    }

    private void FlushFinished()
    {
        // The last frame that reads sector L is floor(L / 16); once that one is behind us
        // (or no later frame can be eligible) the sector cannot change any more.
        while (_written < _received
               && (_nextFrame >= _endFrame || (_written / CdCarrierGeometry.FrameSectors) < _nextFrame))
        {
            byte[] sector = _sectors[_written];
            _inner.Write(sector);
            _sectors.Remove(_written);
            _spare.Push(sector);
            _written++;
        }
    }

    private void ProcessFrame(long frame)
    {
        long first = CdCarrierGeometry.FirstSector(frame);
        for (int index = 0; index < _window.Length; index++)
            _window[index] = _sectors[first + index];
        ReadOnlySpan<byte> sector = CdCarrierGeometry.SlotSector;
        ReadOnlySpan<ushort> offset = CdCarrierGeometry.SlotOffset;
        for (int slot = 0; slot < _symbols.Length; slot++)
        {
            int at = offset[slot];
            _symbols[slot] = CdCarrierGeometry.SymbolOf(
                (byte)(_window[sector[slot]][at] ^ CdDataImageGenerator.PayloadMask(at)));
        }

        CarrierBlockInfo info = _encoder.Encode(_symbols, CdCarrierGeometry.GroupSizes);
        _blocks++;
        _capacityBits += info.CapacityBits;
        _usableBits += CarrierBlockFormat.UsableDataBits(info.CapacityBits);
        if (!info.Used)
            return;
        _carrying++;
        for (int slot = 0; slot < _symbols.Length; slot++)
        {
            byte symbol = _symbols[slot];
            if (symbol > 1)
                continue;
            int at = offset[slot];
            _window[sector[slot]][at] = (byte)(CdCarrierGeometry.PaletteOf(symbol) ^ CdDataImageGenerator.PayloadMask(at));
        }
    }
}

public sealed record CdBitCarrierExtractProgress(long FramesScanned, long TotalFrames, int FramesAccepted);

public static class CdBitCarrierExtractor
{
    /// <summary>
    /// Scans a Mode 1 data image (the ISO, or a disc read from LBA 0 in 2048-byte sectors) for
    /// carrier frames and reassembles the embedded stream into <paramref name="output"/>.
    /// </summary>
    /// <param name="iso">Cooked 2048-byte sectors from LBA 0.</param>
    /// <param name="output">Readable, writable, seekable destination for the user data.</param>
    /// <param name="report">Optional: filled with where frames are missing and which sectors look unread.</param>
    public static CarrierExtractResult Extract(
        Stream iso,
        Stream output,
        Action<CdBitCarrierExtractProgress>? progress = null,
        CancellationToken cancellationToken = default,
        CarrierScanReport? report = null)
    {
        ArgumentNullException.ThrowIfNull(iso);
        ArgumentNullException.ThrowIfNull(output);
        if (!iso.CanRead || !iso.CanSeek)
            throw new ArgumentException("The sector image must be readable and seekable.", nameof(iso));

        const int sectorBytes = CdCarrierGeometry.SectorPayloadBytes;
        long totalSectors = iso.Length / sectorBytes;
        long totalFrames = totalSectors < CdCarrierGeometry.SectorSpan
            ? 0
            : ((totalSectors - CdCarrierGeometry.SectorSpan) / CdCarrierGeometry.FrameSectors) + 1;
        int parallelism = Math.Max(1, Environment.ProcessorCount);
        int batch = parallelism * 2;
        byte[] buffer = new byte[checked(((batch * CdCarrierGeometry.FrameSectors) + CdCarrierGeometry.SectorSpan) * sectorBytes)];
        CarrierBlock?[] decoded = new CarrierBlock?[batch];
        int[] capacities = new int[batch];
        double[] validShare = new double[batch];
        CarrierAssembler assembler = new(output);
        ParallelOptions options = new() { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken };
        using ThreadLocal<(CarrierBlockDecoder Decoder, byte[] Symbols)> workers = new(
            () => (new CarrierBlockDecoder(), new byte[CdCarrierGeometry.SlotsPerFrame]));
        ReadOnlySpan<byte> groups = CdCarrierGeometry.GroupSizes;
        byte[] groupSizes = groups.ToArray();
        byte[] slotSector = CdCarrierGeometry.SlotSector.ToArray();
        ushort[] slotOffset = CdCarrierGeometry.SlotOffset.ToArray();
        byte[] mask = new byte[sectorBytes];
        for (int index = 0; index < mask.Length; index++)
            mask[index] = CdDataImageGenerator.PayloadMask(index);

        long frame = 0;
        while (frame < totalFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = (int)Math.Min(batch, totalFrames - frame);
            int sectors = (count * CdCarrierGeometry.FrameSectors) + CdCarrierGeometry.SectorSpan - CdCarrierGeometry.FrameSectors;
            iso.Position = frame * CdCarrierGeometry.FrameSectors * sectorBytes;
            iso.ReadExactly(buffer, 0, sectors * sectorBytes);
            long batchStart = frame;
            Parallel.For(0, count, options, index =>
            {
                (CarrierBlockDecoder decoder, byte[] symbols) = workers.Value;
                int firstSector = index * CdCarrierGeometry.FrameSectors;
                for (int slot = 0; slot < symbols.Length; slot++)
                {
                    int at = slotOffset[slot];
                    byte cooked = buffer[((firstSector + slotSector[slot]) * sectorBytes) + at];
                    symbols[slot] = CdCarrierGeometry.SymbolOf((byte)(cooked ^ mask[at]));
                }

                decoded[index] = decoder.TryDecode(symbols, groupSizes, out CarrierBlock? block) ? block : null;
                capacities[index] = decoder.LastCapacityBits;
                validShare[index] = decoder.LastValidGroupFraction;
            });
            for (int index = 0; index < count; index++)
            {
                if (decoded[index] is { } block)
                    assembler.Add(block);
                if (report is null)
                    continue;
                report.AddFrame(decoded[index] is not null, capacities[index], validShare[index]);
                for (int sector = 0; sector < CdCarrierGeometry.FrameSectors; sector++)
                {
                    int at = ((index * CdCarrierGeometry.FrameSectors) + sector) * sectorBytes;
                    if (buffer.AsSpan(at, sectorBytes).IndexOfAnyExcept((byte)0) < 0)
                        report.AddZeroSector(((batchStart + index) * CdCarrierGeometry.FrameSectors) + sector);
                }
            }

            frame += count;
            progress?.Invoke(new CdBitCarrierExtractProgress(frame, totalFrames, assembler.BlocksAccepted));
        }

        report?.Complete(frame => new LbaRange(
            frame * CdCarrierGeometry.FrameSectors, (frame + 1) * CdCarrierGeometry.FrameSectors));
        return assembler.Finish();
    }
}
