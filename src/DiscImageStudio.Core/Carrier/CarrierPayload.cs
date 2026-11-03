using System.Buffers.Binary;

namespace DiscImageStudio.Core.Carrier;

/// <summary>
/// The logical stream placed on the disc: a 20-byte container header followed by the
/// user's bytes.
///
/// <code>
/// "DBPC" | version(1) | flags(1) | reserved(2) | length(8, big-endian) | crc32(4) | data…
/// </code>
/// </summary>
public static class CarrierContainer
{
    public const int HeaderBytes = 20;
    public const byte Version = 1;
    private static readonly byte[] Magic = "DBPC"u8.ToArray();

    public static byte[] BuildHeader(long length, uint crc32)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        byte[] header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8, 8), (ulong)length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16, 4), crc32);
        return header;
    }

    public static bool TryParseHeader(ReadOnlySpan<byte> header, out long length, out uint crc32)
    {
        length = 0;
        crc32 = 0;
        if (header.Length < HeaderBytes
            || !header[..4].SequenceEqual(Magic)
            || header[4] != Version)
        {
            return false;
        }

        ulong declared = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
        if (declared > (CarrierBlockFormat.MaxStreamBits / 8) - HeaderBytes)
            return false;
        length = (long)declared;
        crc32 = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(16, 4));
        return true;
    }
}

/// <summary>
/// An <see cref="ICarrierBitSource"/> over a seekable stream. The CRC-32 of the data is
/// computed once up front so every block can find the container header at offset 0.
/// </summary>
public sealed class CarrierPayload : ICarrierBitSource, IDisposable
{
    private readonly Stream _data;
    private readonly bool _leaveOpen;
    private readonly byte[] _header;
    private readonly long _length;

    public CarrierPayload(Stream data, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!data.CanRead || !data.CanSeek)
            throw new ArgumentException("Payload stream must be readable and seekable.", nameof(data));
        _data = data;
        _leaveOpen = leaveOpen;
        _length = data.Length;
        _data.Position = 0;
        uint crc = CarrierCrc32.Initial;
        byte[] buffer = new byte[81920];
        int read;
        while ((read = _data.Read(buffer, 0, buffer.Length)) > 0)
            crc = CarrierCrc32.Update(crc, buffer.AsSpan(0, read));
        _header = CarrierContainer.BuildHeader(_length, CarrierCrc32.Finish(crc));
        TotalBits = checked((ulong)(CarrierContainer.HeaderBytes + _length) * 8);
    }

    public static CarrierPayload FromFile(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.RandomAccess));

    public static CarrierPayload FromBytes(byte[] data)
        => new(new MemoryStream(data, writable: false));

    /// <summary>Length of the user data, without the container header.</summary>
    public long DataLength => _length;

    public ulong TotalBits { get; }

    public void ReadBits(ulong offset, int count, Span<byte> destination)
    {
        if (count < 0 || destination.Length < (count + 7) / 8)
            throw new ArgumentException("Destination is too small.", nameof(destination));
        if (count == 0)
            return;
        ulong firstByte = offset / 8;
        int shift = (int)(offset % 8);
        int span = (shift + count + 7) / 8;
        Span<byte> raw = span <= 512 ? stackalloc byte[span] : new byte[span];
        raw.Clear();
        for (int index = 0; index < span; index++)
        {
            ulong position = firstByte + (ulong)index;
            if (position < (ulong)CarrierContainer.HeaderBytes)
                raw[index] = _header[(int)position];
        }

        long dataStart = (long)Math.Max(firstByte, (ulong)CarrierContainer.HeaderBytes)
            - CarrierContainer.HeaderBytes;
        int rawStart = (int)(Math.Max(firstByte, (ulong)CarrierContainer.HeaderBytes) - firstByte);
        if (rawStart < span && dataStart < _length)
        {
            int wanted = (int)Math.Min(span - rawStart, _length - dataStart);
            _data.Position = dataStart;
            _data.ReadExactly(raw.Slice(rawStart, wanted));
        }

        // Left-align the requested bits and clear everything beyond count.
        for (int index = 0; index < (count + 7) / 8; index++)
        {
            int value = raw[index] << shift;
            if (shift != 0 && index + 1 < span)
                value |= raw[index + 1] >> (8 - shift);
            destination[index] = (byte)value;
        }

        int tail = count & 7;
        if (tail != 0)
            destination[(count - 1) / 8] &= (byte)(0xFF << (8 - tail));
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _data.Dispose();
    }
}

/// <summary>Outcome of reassembling a stream from decoded blocks.</summary>
/// <param name="HeaderFound">The container header (stream offset 0) was recovered.</param>
/// <param name="DataLength">Declared length of the user data, when the header was found.</param>
/// <param name="MissingBits">Container bits not covered by any accepted block.</param>
/// <param name="CrcValid">All data was present and matched the container CRC-32.</param>
public sealed record CarrierExtractResult(
    int BlocksAccepted,
    bool HeaderFound,
    long DataLength,
    ulong MissingBits,
    bool CrcValid)
{
    public bool Complete => HeaderFound && MissingBits == 0 && CrcValid;
}

/// <summary>
/// Reassembles the user data from decoded blocks, in any order. The destination must be a
/// readable, writable, seekable stream; data is written at its final position as soon as a
/// block arrives, so the whole payload never needs to fit in memory.
/// </summary>
public sealed class CarrierAssembler
{
    private const int HeaderBits = CarrierContainer.HeaderBytes * 8;

    private readonly Stream _output;
    private readonly byte[] _header = new byte[CarrierContainer.HeaderBytes];
    private readonly bool[] _headerBitKnown = new bool[HeaderBits];
    private readonly List<(ulong Start, ulong End)> _covered = [];
    private int _accepted;

    public CarrierAssembler(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!output.CanRead || !output.CanWrite || !output.CanSeek)
            throw new ArgumentException("Output stream must be readable, writable and seekable.", nameof(output));
        _output = output;
    }

    public int BlocksAccepted => _accepted;

    public void Add(CarrierBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.DataBits == 0)
            return;
        _accepted++;
        ulong start = block.StreamOffset;
        ulong end = start + (ulong)block.DataBits;
        AddCovered(start, end);

        if (start < HeaderBits)
        {
            int count = (int)Math.Min(end, HeaderBits) - (int)start;
            for (int bit = 0; bit < count; bit++)
            {
                BitBuffer.Set(_header, (int)start + bit, BitBuffer.Get(block.Data, bit));
                _headerBitKnown[(int)start + bit] = true;
            }
        }

        if (end > HeaderBits)
        {
            int skip = (int)(Math.Max(start, HeaderBits) - start);
            WriteBits(Math.Max(start, HeaderBits) - HeaderBits, block.Data, skip, block.DataBits - skip);
        }
    }

    public CarrierExtractResult Finish()
    {
        bool headerKnown = _headerBitKnown.All(known => known);
        if (!headerKnown
            || !CarrierContainer.TryParseHeader(_header, out long length, out uint expectedCrc))
        {
            return new CarrierExtractResult(_accepted, false, 0, 0, false);
        }

        ulong total = (ulong)(CarrierContainer.HeaderBytes + length) * 8;
        ulong covered = 0;
        foreach ((ulong start, ulong end) in _covered)
        {
            if (start >= total)
                continue;
            covered += Math.Min(end, total) - start;
        }

        ulong missing = total - covered;
        if (_output.Length != length)
            _output.SetLength(length);
        bool crcValid = false;
        if (missing == 0)
        {
            _output.Position = 0;
            uint crc = CarrierCrc32.Initial;
            byte[] buffer = new byte[81920];
            int read;
            while ((read = _output.Read(buffer, 0, buffer.Length)) > 0)
                crc = CarrierCrc32.Update(crc, buffer.AsSpan(0, read));
            crcValid = CarrierCrc32.Finish(crc) == expectedCrc;
        }

        _output.Flush();
        return new CarrierExtractResult(_accepted, true, length, missing, crcValid);
    }

    /// <summary>Bit ranges (in container coordinates) that no accepted block covered.</summary>
    public IReadOnlyList<(ulong Start, ulong End)> CoveredRanges => _covered;

    private void WriteBits(ulong dataBitOffset, byte[] source, int sourceBit, int count)
    {
        if (count <= 0)
            return;
        int destShift = (int)(dataBitOffset % 8);
        long firstByte = (long)(dataBitOffset / 8);
        int destBytes = (destShift + count + 7) / 8;
        byte[] merged = new byte[destBytes];
        for (int index = 0; index < count; index++)
        {
            if (BitBuffer.Get(source, sourceBit + index) != 0)
                BitBuffer.Set(merged, destShift + index, 1);
        }

        // OR into existing bytes so neighbouring blocks that share a byte combine.
        if (firstByte < _output.Length)
        {
            int existing = (int)Math.Min(destBytes, _output.Length - firstByte);
            byte[] current = new byte[existing];
            _output.Position = firstByte;
            _output.ReadExactly(current);
            for (int index = 0; index < existing; index++)
                merged[index] |= current[index];
        }

        if (_output.Length < firstByte)
            _output.SetLength(firstByte); // Zero-filled hole before this block.
        _output.Position = firstByte;
        _output.Write(merged);
    }

    private void AddCovered(ulong start, ulong end)
    {
        // Typical input is contiguous and ascending, so merge against the tail first.
        for (int index = 0; index < _covered.Count; index++)
        {
            (ulong s, ulong e) = _covered[index];
            if (start <= e && end >= s)
            {
                ulong mergedStart = Math.Min(s, start);
                ulong mergedEnd = Math.Max(e, end);
                _covered.RemoveAt(index);
                AddCovered(mergedStart, mergedEnd);
                return;
            }
        }

        _covered.Add((start, end));
    }
}
