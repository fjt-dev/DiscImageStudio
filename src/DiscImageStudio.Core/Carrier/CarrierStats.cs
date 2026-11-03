namespace DiscImageStudio.Core.Carrier;

/// <summary>Running totals of an embedding pass (medium independent).</summary>
/// <param name="BlocksProcessed">Frames that could carry data (eligible blocks) seen so far.</param>
/// <param name="CapacityBits">Raw bits the arrangements of all those blocks can hold, headers included.</param>
/// <param name="UsableDataBits">Stream bits they can carry once their frame headers are paid.</param>
public sealed record CarrierStats(
    long BlocksProcessed,
    long BlocksCarrying,
    long CapacityBits,
    long UsableDataBits,
    ulong StreamBitsCarried,
    ulong StreamBitsTotal)
{
    public bool Complete => StreamBitsCarried >= StreamBitsTotal;

    /// <summary>Largest file this drawing can hold, container header excluded.</summary>
    public long MaxFileBytes => Math.Max(0, (UsableDataBits / 8) - CarrierContainer.HeaderBytes);

    /// <summary>The file bytes stored so far (container header excluded).</summary>
    public long StoredFileBytes => Math.Max(0, (long)(StreamBitsCarried / 8) - CarrierContainer.HeaderBytes);

    /// <summary>The size of the file being stored.</summary>
    public long TotalFileBytes => Math.Max(0, (long)(StreamBitsTotal / 8) - CarrierContainer.HeaderBytes);
}

/// <summary>
/// What a drawing can hold, measured without writing it. Exact, not an estimate: embedding
/// never changes how many light symbols a group has, and the measurement builds the symbols
/// exactly as generation does.
/// </summary>
/// <param name="PayloadBytesPerBlock">Drawn payload bytes in one block (for a bits-per-byte figure).</param>
public sealed record CarrierCapacity(
    long Blocks, long BlocksCarrying, long RawBits, long DataBits, int PayloadBytesPerBlock)
{
    /// <summary>Largest file that fits, container header excluded.</summary>
    public long MaxFileBytes => Math.Max(0, (DataBits / 8) - CarrierContainer.HeaderBytes);

    /// <summary>Average stored bits per drawn payload byte.</summary>
    public double BitsPerByte => Blocks == 0 || PayloadBytesPerBlock == 0 ? 0
        : (double)DataBits / (Blocks * (double)PayloadBytesPerBlock);

    public bool Fits(long fileBytes) => fileBytes <= MaxFileBytes;
}
