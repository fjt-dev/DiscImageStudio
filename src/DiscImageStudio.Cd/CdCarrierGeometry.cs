namespace DiscImageStudio.Cd;

/// <summary>
/// Where the payload bytes of a Mode 1 sector sit on the disc, for the bit-per-byte carrier.
///
/// CIRC interleaving scatters the 2048 payload bytes of one sector over about two sectors
/// of track (roughly 30 mm), so bytes that are neighbours in the file are not neighbours
/// on the disc, and bytes of different sectors share a spot. Re-ordering light and dark
/// bytes is only invisible when it happens between bytes that really are next to each
/// other. The carrier therefore works in physical order: a <em>frame</em> is the 16 sectors'
/// worth of track between two multiples of 16 sectors, its payload bytes are sorted by
/// their position on the track, and a <em>group</em> is the payload bytes that fall into
/// one 16-byte window of that track (9–16 of them; the rest of the window is header,
/// EDC and ECC bytes that nobody controls).
///
/// Because 2352 bytes = 98 CIRC frames, the layout is identical for every frame: one table
/// serves the whole disc. Frame <c>f</c> takes its bytes from sectors <c>16f … 16f + 17</c>.
/// </summary>
public static class CdCarrierGeometry
{
    public const int FrameSectors = 16;

    /// <summary>Frame <c>f</c> reads sectors <c>16f … 16f + SectorSpan - 1</c>.</summary>
    public const int SectorSpan = 18;

    public const int SectorPayloadBytes = CdDataImageGenerator.BytesPerSector;

    /// <summary>Payload bytes per frame: 16 sectors of 2048.</summary>
    public const int SlotsPerFrame = FrameSectors * SectorPayloadBytes;

    public const int RawBytesPerSector = CdDiscParameters.BytesPerSector;

    private const int WindowBytes = 16;
    private const int BaseSector = 32; // far enough from the start that no position is negative.

    private static readonly Table Layout = BuildTable();

    /// <summary>Sizes of the groups, in physical order; they add up to <see cref="SlotsPerFrame"/>.</summary>
    public static ReadOnlySpan<byte> GroupSizes => Layout.GroupSizes;

    /// <summary>Sector (0 … 17, relative to the frame's first sector) of each slot, in physical order.</summary>
    internal static ReadOnlySpan<byte> SlotSector => Layout.Sector;

    /// <summary>Payload offset (0 … 2047) of each slot within its sector.</summary>
    internal static ReadOnlySpan<ushort> SlotOffset => Layout.Offset;

    /// <summary>Raw byte position of each slot relative to the frame start.</summary>
    internal static ReadOnlySpan<int> SlotPosition => Layout.Position;

    /// <summary>First sector of frame <paramref name="frame"/>.</summary>
    public static long FirstSector(long frame) => frame * FrameSectors;

    /// <summary>The frame's byte position on the track for slot <paramref name="slot"/>.</summary>
    internal static long PhysicalByte(long frame, int slot)
        => (frame * FrameSectors * RawBytesPerSector) + Layout.Position[slot];

    /// <summary>
    /// A frame can carry data only when every sector it reads belongs to the drawing:
    /// anything else would rearrange bytes of the filesystem.
    /// </summary>
    public static bool IsEligible(long frame, long drawingStart, long drawingEndExclusive)
        => FirstSector(frame) >= drawingStart
            && FirstSector(frame) + SectorSpan <= drawingEndExclusive;

    /// <summary>First frame that can be eligible for a drawing starting at <paramref name="drawingStart"/>.</summary>
    public static long FirstEligibleFrame(long drawingStart)
        => (drawingStart + FrameSectors - 1) / FrameSectors;

    /// <summary>One past the last eligible frame (equal to the first when none is).</summary>
    public static long EndEligibleFrame(long drawingStart, long drawingEndExclusive)
    {
        long first = FirstEligibleFrame(drawingStart);
        long last = ((drawingEndExclusive - SectorSpan) / FrameSectors) + 1;
        return drawingEndExclusive < SectorSpan ? first : Math.Max(first, last);
    }

    /// <summary>Palette byte → symbol: 0x0D dark = 0, 0xAA light = 1, anything else invalid.</summary>
    internal static byte SymbolOf(byte palette) => palette switch
    {
        0x0D => 0,
        0xAA => 1,
        _ => (byte)2,
    };

    internal static byte PaletteOf(byte symbol) => symbol == 1 ? (byte)0xAA : (byte)0x0D;

    private static Table BuildTable()
    {
        long frameBase = (long)BaseSector * RawBytesPerSector;
        long frameBytes = (long)FrameSectors * RawBytesPerSector;
        List<(int Position, byte Sector, ushort Offset)> slots = new(SlotsPerFrame);
        for (int sector = 0; sector < SectorSpan; sector++)
        {
            for (int offset = 0; offset < SectorPayloadBytes; offset++)
            {
                long physical = CdDataImageGenerator.PhysicalByteForPayload(BaseSector + sector, offset);
                if (physical >= frameBase && physical < frameBase + frameBytes)
                    slots.Add(((int)(physical - frameBase), (byte)sector, (ushort)offset));
            }
        }

        if (slots.Count != SlotsPerFrame)
        {
            throw new InvalidOperationException(
                $"CD frame layout has {slots.Count} payload bytes, expected {SlotsPerFrame}.");
        }

        slots.Sort((a, b) => a.Position.CompareTo(b.Position));
        for (int index = 1; index < slots.Count; index++)
        {
            if (slots[index].Position == slots[index - 1].Position)
                throw new InvalidOperationException("Two payload bytes share one position on the track.");
        }

        byte[] sectors = new byte[SlotsPerFrame];
        ushort[] offsets = new ushort[SlotsPerFrame];
        int[] positions = new int[SlotsPerFrame];
        List<byte> sizes = [];
        int window = -1, inWindow = 0;
        for (int index = 0; index < slots.Count; index++)
        {
            sectors[index] = slots[index].Sector;
            offsets[index] = slots[index].Offset;
            positions[index] = slots[index].Position;
            int current = slots[index].Position / WindowBytes;
            if (current != window)
            {
                if (inWindow > 0)
                    sizes.Add((byte)inWindow);
                window = current;
                inWindow = 0;
            }

            inWindow++;
        }

        sizes.Add((byte)inWindow);
        return new Table(sectors, offsets, positions, sizes.ToArray());
    }

    private sealed record Table(byte[] Sector, ushort[] Offset, int[] Position, byte[] GroupSizes);
}
