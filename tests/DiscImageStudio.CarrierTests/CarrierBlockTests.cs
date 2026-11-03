using DiscImageStudio.Core.Carrier;

internal static class CarrierBlockTests
{
    private const int BlockSymbols = 32768;

    internal static void Run()
    {
        TestRoundTripPreservesDensity();
        TestUnusedAndForeignBlocks();
        TestLostBlockOnlyCostsItsOwnBits();
        TestOutOfOrderAssembly();
        Console.WriteLine("carrier-block: passed (round trip, per-group density preserved, foreign data rejected, loss isolation, any-order assembly)");
    }

    /// <summary>Random symbols with a controllable mix of densities and invalid groups.</summary>
    internal static byte[] RandomSymbols(Random random, double invalidGroupChance = 0.02)
    {
        byte[] symbols = new byte[BlockSymbols];
        for (int group = 0; group < BlockSymbols / 16; group++)
        {
            double density = random.NextDouble();
            for (int index = 0; index < 16; index++)
                symbols[group * 16 + index] = random.NextDouble() < density ? (byte)1 : (byte)0;
            if (random.NextDouble() < invalidGroupChance)
                symbols[group * 16 + random.Next(16)] = CarrierBlockFormat.InvalidSymbol;
        }

        return symbols;
    }

    private static void TestRoundTripPreservesDensity()
    {
        Random random = new(7);
        byte[] data = new byte[40_000];
        random.NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        CarrierBlockEncoder encoder = new(payload);
        CarrierBlockDecoder decoder = new();
        using MemoryStream output = new();
        CarrierAssembler assembler = new(output);
        int blocks = 0;
        bool differs = false;
        while (!encoder.IsComplete)
        {
            byte[] original = RandomSymbols(random);
            byte[] symbols = (byte[])original.Clone();
            CarrierBlockInfo info = encoder.Encode(symbols);
            Check.True(info.Used && info.DataBits > 0, "a block with ample capacity carries data");
            for (int group = 0; group < BlockSymbols / 16; group++)
            {
                int before = 0, after = 0;
                bool invalid = false;
                for (int index = 0; index < 16; index++)
                {
                    byte symbol = original[group * 16 + index];
                    invalid |= symbol > 1;
                    before += symbol > 1 ? 0 : symbol;
                    after += symbol > 1 ? 0 : symbols[group * 16 + index];
                }

                Check.Equal(before, after, "white count of every group is unchanged");
                if (invalid)
                {
                    Check.True(original.AsSpan(group * 16, 16).SequenceEqual(symbols.AsSpan(group * 16, 16)),
                        "groups with invalid symbols are not touched");
                }
            }

            differs |= !original.AsSpan().SequenceEqual(symbols);
            Check.True(decoder.TryDecode(symbols, out CarrierBlock? block) && block!.StreamOffset == info.StreamOffset
                && block.DataBits == info.DataBits, "block decodes to what was encoded");
            assembler.Add(block!);
            blocks++;
            Check.True(blocks < 100, "payload must finish");
        }

        Check.True(differs, "embedding actually rearranges symbols");
        CarrierExtractResult result = assembler.Finish();
        Check.True(result.Complete, "stream is complete and its CRC matches");
        Check.True(output.ToArray().SequenceEqual(data), "extracted bytes equal the input");
    }

    private static void TestUnusedAndForeignBlocks()
    {
        Random random = new(11);
        using CarrierPayload payload = CarrierPayload.FromBytes(new byte[100]);
        CarrierBlockEncoder encoder = new(payload);

        // All-dark: zero capacity, nothing to carry, block stays untouched.
        byte[] dark = new byte[BlockSymbols];
        CarrierBlockInfo info = encoder.Encode(dark);
        Check.True(!info.Used && info.CapacityBits == 0 && dark.All(symbol => symbol == 0),
            "a flat block is left alone");
        Check.Equal(0UL, encoder.NextOffset, "an unused block does not advance the stream");

        // Too small to hold even the header.
        byte[] small = RandomSymbols(random);
        Array.Resize(ref small, 16 * 4); // 4 groups x 13 bits = 52 < 96 header bits
        for (int index = 0; index < small.Length; index++)
            small[index] = (byte)(index % 2);
        Check.True(!encoder.Encode(small).Used, "a block smaller than its header is skipped");

        CarrierBlockDecoder decoder = new();
        int accepted = 0;
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] noise = RandomSymbols(random, invalidGroupChance: 0);
            if (decoder.TryDecode(noise, out _))
                accepted++;
        }

        Check.Equal(0, accepted, "unrelated random blocks are never accepted");
        Check.True(!decoder.TryDecode(dark, out _), "an empty block is not a frame");
    }

    private static void TestLostBlockOnlyCostsItsOwnBits()
    {
        Random random = new(23);
        byte[] data = new byte[30_000];
        random.NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        CarrierBlockEncoder encoder = new(payload);
        List<(CarrierBlockInfo Info, byte[] Symbols)> blocks = [];
        while (!encoder.IsComplete)
        {
            byte[] symbols = RandomSymbols(random);
            blocks.Add((encoder.Encode(symbols), symbols));
        }

        Check.True(blocks.Count >= 4, "test needs several blocks");
        int lost = blocks.Count / 2;
        CarrierBlockDecoder decoder = new();
        using MemoryStream output = new();
        CarrierAssembler assembler = new(output);
        for (int index = 0; index < blocks.Count; index++)
        {
            if (index == lost)
                continue;
            Check.True(decoder.TryDecode(blocks[index].Symbols, out CarrierBlock? block), "surviving block decodes");
            assembler.Add(block!);
        }

        CarrierExtractResult result = assembler.Finish();
        Check.True(result.HeaderFound && !result.Complete, "losing a block makes the result incomplete");
        Check.Equal((ulong)blocks[lost].Info.DataBits, result.MissingBits, "exactly the lost block's bits are missing");
        byte[] extracted = output.ToArray();
        ulong lostStart = blocks[lost].Info.StreamOffset - (ulong)CarrierContainer.HeaderBytes * 8;
        int firstBad = (int)(lostStart / 8);
        Check.True(extracted.AsSpan(0, firstBad).SequenceEqual(data.AsSpan(0, firstBad)),
            "data before the lost block is intact");
        int resume = (int)((lostStart + (ulong)blocks[lost].Info.DataBits + 7) / 8);
        Check.True(extracted.AsSpan(resume).SequenceEqual(data.AsSpan(resume)),
            "data after the lost block is still placed correctly");
    }

    private static void TestOutOfOrderAssembly()
    {
        Random random = new(31);
        byte[] data = new byte[20_000];
        random.NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        CarrierBlockEncoder encoder = new(payload);
        CarrierBlockDecoder decoder = new();
        List<CarrierBlock> blocks = [];
        while (!encoder.IsComplete)
        {
            byte[] symbols = RandomSymbols(random);
            encoder.Encode(symbols);
            Check.True(decoder.TryDecode(symbols, out CarrierBlock? block), "decodes");
            blocks.Add(block!);
        }

        using MemoryStream output = new();
        CarrierAssembler assembler = new(output);
        foreach (CarrierBlock block in blocks.OrderBy(_ => random.Next()))
            assembler.Add(block);
        // Duplicates must be harmless.
        assembler.Add(blocks[1]);
        Check.True(assembler.Finish().Complete && output.ToArray().SequenceEqual(data),
            "blocks may arrive in any order and more than once");
    }
}
