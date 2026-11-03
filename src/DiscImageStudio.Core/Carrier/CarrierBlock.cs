using System.Buffers.Binary;

namespace DiscImageStudio.Core.Carrier;

/// <summary>A source of payload bits addressed by absolute bit offset (MSB-first).</summary>
public interface ICarrierBitSource
{
    ulong TotalBits { get; }

    /// <summary>
    /// Reads <paramref name="count"/> bits starting at bit <paramref name="offset"/> into
    /// <paramref name="destination"/>, MSB-first. Bits past the end of the stream and the
    /// unused tail of the last byte are zero.
    /// </summary>
    void ReadBits(ulong offset, int count, Span<byte> destination);
}

/// <summary>Result of presenting one block of pattern symbols to the encoder.</summary>
/// <param name="Used">True when the block now carries a frame.</param>
/// <param name="StreamOffset">Absolute stream bit offset of the first data bit in the frame.</param>
/// <param name="DataBits">Stream bits carried (zero for an unused block).</param>
/// <param name="CapacityBits">Raw bits the arrangement of the block can hold, header included.</param>
public readonly record struct CarrierBlockInfo(
    bool Used, ulong StreamOffset, int DataBits, int CapacityBits);

/// <summary>A successfully decoded frame.</summary>
public sealed record CarrierBlock(ulong StreamOffset, int DataBits, byte[] Data);

/// <summary>
/// Frame layout of one block (a run of pattern symbols, for DVD one 32 KiB ECC block).
/// The block's groups are visited in order and each valid group contributes
/// <see cref="CombinadicGroup.CapacityBits"/> bits, forming one bit sequence:
///
/// <code>
/// | header: 96 bits, whitened | data: DataBits, whitened | filler: whitened zeros |
/// header = version(8) | streamOffset(40) | dataBits(16) | crc32(32)
/// </code>
///
/// The CRC covers the first 8 header bytes and the plain data bits. Every block is
/// self-contained: it names the stream offset it carries, so a block that cannot be
/// read costs only its own bits and never shifts the blocks behind it.
/// </summary>
public static class CarrierBlockFormat
{
    public const byte Version = 1;
    public const int HeaderBits = 96;
    public const int HeaderBytes = HeaderBits / 8;
    internal const int PrefixBytes = 8;
    public const int MaxDataBits = 0xFFFF;
    public const ulong MaxStreamBits = (1UL << 40) - 1;

    /// <summary>Marks a symbol that is not one of the two pattern symbols.</summary>
    public const byte InvalidSymbol = 2;

    /// <summary>
    /// Raw bits the arrangement of <paramref name="symbols"/> can hold (frame header included).
    /// Embedding never changes this number, so it can be measured before anything is written.
    /// </summary>
    public static int CountCapacity(ReadOnlySpan<byte> symbols)
        => CountCapacity(symbols, UniformGroups(symbols.Length));

    /// <summary>Stream bits one block can carry, given its raw capacity (0 when it cannot hold a frame).</summary>
    public static int UsableDataBits(int capacityBits)
        => capacityBits <= HeaderBits ? 0 : Math.Min(capacityBits - HeaderBits, MaxDataBits);

    /// <summary>
    /// Raw bits for symbols split into groups of the given sizes (each 0..16, in order,
    /// summing to the symbol count). A group with any invalid symbol carries nothing.
    /// </summary>
    public static int CountCapacity(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> groupSizes)
    {
        ValidateGroups(symbols.Length, groupSizes);
        int capacity = 0;
        int start = 0;
        foreach (byte size in groupSizes)
        {
            int ones = 0;
            bool valid = true;
            foreach (byte symbol in symbols.Slice(start, size))
            {
                if (symbol > 1)
                {
                    valid = false;
                    break;
                }

                ones += symbol;
            }

            if (valid)
                capacity += CombinadicGroup.CapacityBits(size, ones);
            start += size;
        }

        return capacity;
    }

    internal static int CountCapacity(
        ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> groupSizes, Span<sbyte> onesByGroup)
    {
        int capacity = 0;
        int start = 0;
        for (int group = 0; group < groupSizes.Length; group++)
        {
            int size = groupSizes[group];
            int ones = 0;
            bool valid = true;
            foreach (byte symbol in symbols.Slice(start, size))
            {
                if (symbol > 1)
                {
                    valid = false;
                    break;
                }

                ones += symbol;
            }

            onesByGroup[group] = valid ? (sbyte)ones : (sbyte)-1;
            if (valid)
                capacity += CombinadicGroup.CapacityBits(size, ones);
            start += size;
        }

        return capacity;
    }

    internal static void ValidateLength(int symbolCount)
    {
        if (symbolCount == 0 || symbolCount % CombinadicGroup.Size != 0)
            throw new ArgumentException(
                $"Symbol count must be a positive multiple of {CombinadicGroup.Size}.");
    }

    internal static void ValidateGroups(int symbolCount, ReadOnlySpan<byte> groupSizes)
    {
        int total = 0;
        foreach (byte size in groupSizes)
        {
            if (size > CombinadicGroup.Size)
                throw new ArgumentException($"A group holds at most {CombinadicGroup.Size} symbols.");
            total += size;
        }

        if (total != symbolCount || symbolCount == 0)
            throw new ArgumentException("Group sizes must add up to the (positive) symbol count.");
    }

    internal static byte[] UniformGroups(int symbolCount)
    {
        ValidateLength(symbolCount);
        byte[] sizes = new byte[symbolCount / CombinadicGroup.Size];
        Array.Fill(sizes, (byte)CombinadicGroup.Size);
        return sizes;
    }

    internal static uint FrameCrc(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> data)
        => CarrierCrc32.Finish(CarrierCrc32.Update(
            CarrierCrc32.Update(CarrierCrc32.Initial, prefix), data));
}

/// <summary>
/// Writes data into the arrangement of pattern symbols. Not thread-safe: blocks must be
/// presented one at a time, in disc order.
/// </summary>
public sealed class CarrierBlockEncoder
{
    private readonly ICarrierBitSource _source;
    private sbyte[] _ones = [];
    private byte[] _frame = [];
    private byte[] _uniform = [];
    private byte[] _data = new byte[(CarrierBlockFormat.MaxDataBits + 7) / 8];

    public CarrierBlockEncoder(ICarrierBitSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.TotalBits > CarrierBlockFormat.MaxStreamBits)
            throw new ArgumentException("Payload exceeds the 40-bit stream offset range.", nameof(source));
        _source = source;
    }

    /// <summary>Stream bit offset the next frame will carry.</summary>
    public ulong NextOffset { get; private set; }

    public bool IsComplete => NextOffset >= _source.TotalBits;

    /// <summary>
    /// Rearranges the white symbols inside every valid group so the block carries the next
    /// piece of the stream. The number of white symbols per group never changes.
    /// <paramref name="symbols"/> holds 0 (dark), 1 (light) or <see cref="CarrierBlockFormat.InvalidSymbol"/>,
    /// split into groups of 16.
    /// </summary>
    public CarrierBlockInfo Encode(Span<byte> symbols)
    {
        CarrierBlockFormat.ValidateLength(symbols.Length);
        if (_uniform.Length * CombinadicGroup.Size != symbols.Length)
            _uniform = CarrierBlockFormat.UniformGroups(symbols.Length);
        return Encode(symbols, _uniform);
    }

    /// <summary>
    /// Same, for groups of the given sizes (each 0..16, in order). Symbols that sit close
    /// together on the disc form one group, so the groups of a medium whose bytes are
    /// interleaved need not be runs of consecutive symbols.
    /// </summary>
    public CarrierBlockInfo Encode(Span<byte> symbols, ReadOnlySpan<byte> groupSizes)
    {
        CarrierBlockFormat.ValidateGroups(symbols.Length, groupSizes);
        int groups = groupSizes.Length;
        if (_ones.Length != groups)
        {
            _ones = new sbyte[groups];
        }

        int capacity = CarrierBlockFormat.CountCapacity(symbols, groupSizes, _ones);
        if (IsComplete || capacity <= CarrierBlockFormat.HeaderBits)
            return new CarrierBlockInfo(false, NextOffset, 0, capacity);

        ulong remaining = _source.TotalBits - NextOffset;
        int dataBits = (int)Math.Min(
            Math.Min((ulong)(capacity - CarrierBlockFormat.HeaderBits), remaining),
            CarrierBlockFormat.MaxDataBits);
        ulong offset = NextOffset;
        int dataBytes = (dataBits + 7) / 8;
        int frameBytes = (capacity + 7) / 8;
        if (_frame.Length < frameBytes)
            _frame = new byte[frameBytes];
        Span<byte> frame = _frame.AsSpan(0, frameBytes);
        frame.Clear();

        Span<byte> data = _data.AsSpan(0, dataBytes);
        data.Clear();
        _source.ReadBits(offset, dataBits, data);

        Span<byte> prefix = stackalloc byte[CarrierBlockFormat.PrefixBytes];
        prefix[0] = CarrierBlockFormat.Version;
        prefix[1] = (byte)(offset >> 32);
        BinaryPrimitives.WriteUInt32BigEndian(prefix[2..6], (uint)offset);
        BinaryPrimitives.WriteUInt16BigEndian(prefix[6..8], (ushort)dataBits);
        uint crc = CarrierBlockFormat.FrameCrc(prefix, data);

        Span<byte> header = stackalloc byte[CarrierBlockFormat.HeaderBytes];
        prefix.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..12], crc);
        for (int bit = 0; bit < CarrierBlockFormat.HeaderBits; bit++)
        {
            BitBuffer.Set(frame, bit,
                BitBuffer.Get(header, bit) ^ CarrierWhitening.Bit((ulong)bit, header: true));
        }

        for (int index = 0; index < capacity - CarrierBlockFormat.HeaderBits; index++)
        {
            int plain = index < dataBits ? BitBuffer.Get(data, index) : 0;
            BitBuffer.Set(frame, CarrierBlockFormat.HeaderBits + index,
                plain ^ CarrierWhitening.Bit(offset + (ulong)index, header: false));
        }

        // Spread the frame bits over the groups.
        int cursor = 0;
        int start = 0;
        for (int group = 0; group < groups; group++)
        {
            int size = groupSizes[group];
            int ones = _ones[group];
            if (ones >= 0)
            {
                int bits = CombinadicGroup.CapacityBits(size, ones);
                uint value = 0;
                for (int bit = 0; bit < bits; bit++)
                    value = (value << 1) | (uint)BitBuffer.Get(frame, cursor++);
                ushort mask = CombinadicGroup.Unrank(size, ones, value);
                Span<byte> target = symbols.Slice(start, size);
                for (int position = 0; position < size; position++)
                    target[position] = (byte)((mask >> position) & 1);
            }

            start += size;
        }

        NextOffset = offset + (ulong)dataBits;
        return new CarrierBlockInfo(true, offset, dataBits, capacity);
    }
}

/// <summary>Recovers frames from pattern symbols. An instance may not be shared across threads.</summary>
public sealed class CarrierBlockDecoder
{
    private sbyte[] _ones = [];
    private byte[] _frame = [];
    private byte[] _uniform = [];

    /// <summary>
    /// Attempts to read a frame from <paramref name="symbols"/> (groups of 16). Ordinary
    /// data, filesystem sectors and damaged blocks fail the group or CRC checks and return false.
    /// </summary>
    public bool TryDecode(ReadOnlySpan<byte> symbols, out CarrierBlock? block)
    {
        CarrierBlockFormat.ValidateLength(symbols.Length);
        if (_uniform.Length * CombinadicGroup.Size != symbols.Length)
            _uniform = CarrierBlockFormat.UniformGroups(symbols.Length);
        return TryDecode(symbols, _uniform, out block);
    }

    /// <summary>Same, for groups of the given sizes; they must match the encoder's.</summary>
    public bool TryDecode(ReadOnlySpan<byte> symbols, ReadOnlySpan<byte> groupSizes, out CarrierBlock? block)
    {
        block = null;
        CarrierBlockFormat.ValidateGroups(symbols.Length, groupSizes);
        int groups = groupSizes.Length;
        if (_ones.Length != groups)
            _ones = new sbyte[groups];
        int capacity = CarrierBlockFormat.CountCapacity(symbols, groupSizes, _ones);
        if (capacity <= CarrierBlockFormat.HeaderBits)
            return false;

        int frameBytes = (capacity + 7) / 8;
        if (_frame.Length < frameBytes)
            _frame = new byte[frameBytes];
        Span<byte> frame = _frame.AsSpan(0, frameBytes);
        frame.Clear();
        int cursor = 0;
        int start = 0;
        for (int group = 0; group < groups; group++)
        {
            int size = groupSizes[group];
            int ones = _ones[group];
            if (ones >= 0)
            {
                ushort mask = 0;
                ReadOnlySpan<byte> source = symbols.Slice(start, size);
                for (int position = 0; position < size; position++)
                    mask |= (ushort)(source[position] << position);
                uint rank = CombinadicGroup.Rank(mask);
                int bits = CombinadicGroup.CapacityBits(size, ones);
                if (rank >> bits != 0)
                    return false; // Not a pattern the encoder can produce.
                for (int bit = bits - 1; bit >= 0; bit--)
                    BitBuffer.Set(frame, cursor++, (int)((rank >> bit) & 1));
            }

            start += size;
        }

        Span<byte> header = stackalloc byte[CarrierBlockFormat.HeaderBytes];
        for (int bit = 0; bit < CarrierBlockFormat.HeaderBits; bit++)
        {
            BitBuffer.Set(header, bit,
                BitBuffer.Get(frame, bit) ^ CarrierWhitening.Bit((ulong)bit, header: true));
        }

        if (header[0] != CarrierBlockFormat.Version)
            return false;
        ulong offset = ((ulong)header[1] << 32) | BinaryPrimitives.ReadUInt32BigEndian(header[2..6]);
        int dataBits = BinaryPrimitives.ReadUInt16BigEndian(header[6..8]);
        if (dataBits > capacity - CarrierBlockFormat.HeaderBits)
            return false;
        uint expected = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);

        byte[] data = new byte[(dataBits + 7) / 8];
        for (int index = 0; index < dataBits; index++)
        {
            BitBuffer.Set(data, index,
                BitBuffer.Get(frame, CarrierBlockFormat.HeaderBits + index)
                ^ CarrierWhitening.Bit(offset + (ulong)index, header: false));
        }

        if (CarrierBlockFormat.FrameCrc(header[..CarrierBlockFormat.PrefixBytes], data) != expected)
            return false;
        block = new CarrierBlock(offset, dataBits, data);
        return true;
    }
}
