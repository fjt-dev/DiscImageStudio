using DiscImageStudio.Core.Carrier;
using DvdImageSolver.Solver;

internal static class DvdBitCarrierTests
{
    private const byte Dark = 0xA5;
    private const byte Light = 0x92;
    private const uint PsnOffset = 0x30000;
    private const int Blocks = 48;

    internal static void Run()
    {
        TestEmbeddingThroughTheWriter();
        TestHybridPrefixAndDamage();
        TestCapacityMeasurementIsExact();
        Console.WriteLine("dvd-bit-carrier: passed (density preserved per 16-byte group, determinism across parallelism, extraction, filesystem prefix, damage isolation, exact capacity measurement)");
    }

    private static MultiBlockSolveOptions Options(uint startLba, int parallelism, IDvdBlockPayloadProcessor? processor)
        => new(StartLba: startLba, FillSectors: Blocks * 16, PsnOffset: PsnOffset,
            IterationsPerBlock: 1, MutationBytes: 4, RandomSeed: 3, InitialTemperature: 64,
            FinalTemperature: 0.25, ZeroInitialPayload: false,
            ImageMapping: new(TotalSectors: 2_297_888, InnerRadiusMm: 23.9968875,
                OuterRadiusMm: 57.9779875, ChannelBitLengthNm: 133.33,
                StartAngleDegrees: 0, Clockwise: true, LuminanceThreshold: 128, AlphaThreshold: 1),
            Algorithm: SolverAlgorithm.Dispersion, FastParallelism: parallelism)
        { PayloadProcessor = processor };

    /// <summary>Scrambled-domain pattern: a smooth density sweep dithered by a hash of the word index.</summary>
    private static byte Pattern(DvdCodewordSample sample)
    {
        // Midtone-heavy, like a photograph: density sweeps 0.2..0.8 along the track.
        double density = 0.5 + (0.3 * Math.Sin(sample.TrackAngleRadians));
        ulong hash = sample.GlobalCodewordIndex * 0x9E37_79B9_7F4A_7C15UL;
        hash ^= hash >> 29;
        hash *= 0xBF58_476D_1CE4_E5B9UL;
        hash ^= hash >> 32;
        return (hash & 0xFFFF) / 65536.0 < density ? Light : Dark;
    }

    private static byte[] Generate(uint startLba, int parallelism, IDvdBlockPayloadProcessor? processor)
    {
        using MemoryStream stream = new();
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(
            Pattern, stream, Options(startLba, parallelism, processor));
        return stream.ToArray();
    }

    private static byte[] IndependentScramblerMask(uint psn)
    {
        ushort[] seeds = [0x0001, 0x5500, 0x0002, 0x2A00, 0x0004, 0x5400, 0x0008, 0x2800,
            0x0010, 0x5000, 0x0020, 0x2001, 0x0040, 0x4002, 0x0080, 0x0005];
        int state = seeds[(psn >> 4) & 15];
        byte[] mask = new byte[2048];
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = (byte)state;
            for (int bit = 0; bit < 8; bit++)
            {
                int incoming = ((state & 0x4000) == 0 ? 0 : 1) ^ ((state & 0x0400) == 0 ? 0 : 1);
                state = ((state * 2) + incoming) & 0x7FFF;
            }
        }

        return mask;
    }

    private static byte[] ToScrambledDomain(byte[] stream, uint startLba)
    {
        byte[] result = new byte[stream.Length];
        for (int sector = 0; sector < stream.Length / 2048; sector++)
        {
            byte[] mask = IndependentScramblerMask(PsnOffset + startLba + (uint)sector);
            for (int index = 0; index < 2048; index++)
                result[sector * 2048 + index] = (byte)(stream[sector * 2048 + index] ^ mask[index]);
        }

        return result;
    }

    private static void TestEmbeddingThroughTheWriter()
    {
        byte[] baseline = Generate(0, parallelism: 1, processor: null);
        byte[] data = new byte[60_000];
        new Random(99).NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        DvdBitCarrierEmbedder embedder = new(payload);
        byte[] embedded = Generate(0, parallelism: 1, embedder);
        DvdBitCarrierStats stats = embedder.Stats;
        Check.True(stats.Complete, $"the pattern holds the whole payload (carried {stats.StreamBitsCarried}/{stats.StreamBitsTotal} bits, {stats.BlocksCarrying}/{stats.BlocksProcessed} blocks, capacity {stats.CapacityBits} bits)");
        Check.Equal((long)Blocks, stats.BlocksProcessed, "every block passes through the hook once");

        // The picture is unchanged: both streams only contain the two pattern bytes, and each
        // group of 16 bytes has the same number of light bytes.
        byte[] qBase = ToScrambledDomain(baseline, 0);
        byte[] qEmbedded = ToScrambledDomain(embedded, 0);
        Check.True(qEmbedded.All(value => value is Dark or Light), "only the two pattern bytes appear");
        bool changed = false;
        for (int group = 0; group < qBase.Length / 16; group++)
        {
            int before = 0, after = 0;
            for (int index = 0; index < 16; index++)
            {
                before += qBase[group * 16 + index] == Light ? 1 : 0;
                after += qEmbedded[group * 16 + index] == Light ? 1 : 0;
            }

            Check.Equal(before, after, $"light-byte count of group {group}");
            changed |= !qBase.AsSpan(group * 16, 16).SequenceEqual(qEmbedded.AsSpan(group * 16, 16));
        }

        Check.True(changed, "the arrangement inside groups differs from the plain dither");

        byte[] parallel = Generate(0, parallelism: 4, new DvdBitCarrierEmbedder(CarrierPayload.FromBytes(data)));
        Check.True(parallel.AsSpan().SequenceEqual(embedded), "parallel generation is bit-identical");

        using MemoryStream output = new();
        CarrierExtractResult result = DvdBitCarrierExtractor.Extract(new MemoryStream(embedded), output, PsnOffset);
        Check.True(result.Complete, "extraction completes with a valid CRC");
        Check.True(output.ToArray().SequenceEqual(data), "extracted bytes equal the embedded file");

        // A plain image carries nothing.
        using MemoryStream none = new();
        CarrierExtractResult plain = DvdBitCarrierExtractor.Extract(new MemoryStream(baseline), none, PsnOffset);
        Check.True(!plain.HeaderFound && plain.BlocksAccepted == 0, "a plain dither contains no frames");
    }

    private static void TestHybridPrefixAndDamage()
    {
        // Filesystem-like random data in front of the drawing, as in a hybrid disc.
        const uint prefixSectors = 64;
        byte[] prefix = new byte[prefixSectors * 2048];
        new Random(5).NextBytes(prefix);
        byte[] data = new byte[50_000];
        new Random(6).NextBytes(data);
        DvdBitCarrierEmbedder embedder = new(CarrierPayload.FromBytes(data));
        byte[] drawing = Generate(prefixSectors, parallelism: 2, embedder);
        byte[] disc = [.. prefix, .. drawing];

        using MemoryStream output = new();
        CarrierExtractResult result = DvdBitCarrierExtractor.Extract(new MemoryStream(disc), output, PsnOffset);
        Check.True(result.Complete && output.ToArray().SequenceEqual(data),
            "a filesystem prefix does not confuse extraction");

        // Wreck one ECC block that carries data: the rest must still be recovered.
        Check.True(embedder.Stats.BlocksCarrying > 4, "the payload spans several blocks");
        byte[] damaged = (byte[])disc.Clone();
        int victim = (int)prefixSectors * 2048 + 3 * 32768;
        new Random(8).NextBytes(damaged.AsSpan(victim, 32768));
        using MemoryStream partial = new();
        CarrierExtractResult loss = DvdBitCarrierExtractor.Extract(new MemoryStream(damaged), partial, PsnOffset);
        Check.True(loss.HeaderFound && !loss.Complete && loss.MissingBits > 0 && !loss.CrcValid,
            "a destroyed block is reported as missing data");
        Check.True(loss.MissingBits < 40_000, "only the destroyed block's bits are missing");
        byte[] recovered = partial.ToArray();
        Check.True(recovered.AsSpan(0, 1000).SequenceEqual(data.AsSpan(0, 1000)),
            "data before the damage survives");
        Check.True(recovered.AsSpan(recovered.Length - 1000).SequenceEqual(data.AsSpan(data.Length - 1000)),
            "data after the damage is still in place");
    }

    private static void TestCapacityMeasurementIsExact()
    {
        MultiBlockSolveOptions options = Options(0, parallelism: 2, processor: null);
        List<FastDvdStreamProgress> updates = [];
        DvdCarrierCapacity capacity = FastDispersionImageWriter.MeasureCarrierCapacity(Pattern, options, updates.Add);
        Check.Equal((long)Blocks, capacity.Blocks, "every block is measured");
        Check.Equal(Blocks, updates.Count, "progress is reported per block");
        Check.True(updates.Select(update => update.CompletedBlocks).SequenceEqual(Enumerable.Range(1, Blocks)),
            "progress arrives in block order");
        Check.True(capacity.MaxFileBytes > 1000 && capacity.BitsPerByte > 0.1, "a midtone pattern has real capacity");
        Check.True(FastDispersionImageWriter.MeasureCarrierCapacity(Pattern, options with { FastParallelism = 1 })
            == capacity, "the measurement does not depend on parallelism");

        // Exactness: the largest file the measurement allows fits, one more byte does not.
        byte[] data = new byte[capacity.MaxFileBytes + 1];
        new Random(77).NextBytes(data);
        for (long size = capacity.MaxFileBytes; size <= capacity.MaxFileBytes + 1; size++)
        {
            using MemoryStream part = new(data, 0, (int)size, writable: false);
            using CarrierPayload payload = new(part);
            DvdBitCarrierEmbedder embedder = new(payload);
            byte[] disc = Generate(0, parallelism: 3, embedder);
            DvdBitCarrierStats stats = embedder.Stats;
            Check.Equal(capacity.RawBits, stats.CapacityBits, "embedding sees the capacity that was measured");
            Check.Equal(capacity.DataBits, stats.UsableDataBits, "usable bits agree with the measurement");
            Check.Equal(capacity.MaxFileBytes, stats.MaxFileBytes, "max file size agrees");
            bool fits = size == capacity.MaxFileBytes;
            Check.Equal(fits, stats.Complete, fits ? "a file of exactly the measured size is stored completely"
                : "one byte more than the measured size cannot be stored");
            if (fits)
            {
                using MemoryStream output = new();
                Check.True(DvdBitCarrierExtractor.Extract(new MemoryStream(disc), output, PsnOffset).Complete
                    && output.ToArray().AsSpan().SequenceEqual(data.AsSpan(0, (int)size)),
                    "and it comes back intact");
            }
        }

        // A drawing with no usable groups holds nothing.
        DvdCarrierCapacity flat = FastDispersionImageWriter.MeasureCarrierCapacity(
            _ => Dark, options);
        Check.Equal(0L, flat.MaxFileBytes, "an all-dark drawing carries nothing");
    }
}
