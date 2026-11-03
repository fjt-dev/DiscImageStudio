using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Carrier;
using DiscImageStudio.ImageIO;

internal static class CdBitCarrierTests
{
    private const int SectorBytes = 2048;

    internal static void Run()
    {
        TestGeometry();
        TestStreamFilterOnSyntheticSectors();
        string directory = Path.Combine(Path.GetTempPath(), "carrier-cd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestGenerationInnerAndOuterFiles(directory);
            TestCapacityIsExact(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("cd-bit-carrier: passed (physical layout, eligibility, density preserved per physical group, filesystem untouched for inner and outer files, extraction, damage isolation, exact capacity measurement)");
    }

    private static void TestGeometry()
    {
        Check.Equal(CdCarrierGeometry.SlotsPerFrame, CdCarrierGeometry.SlotSector.Length, "one slot per payload byte of 16 sectors");
        Check.Equal(CdCarrierGeometry.SlotsPerFrame, CdCarrierGeometry.GroupSizes.ToArray().Sum(size => (int)size), "group sizes cover every slot");
        Check.True(CdCarrierGeometry.GroupSizes.ToArray().All(size => size is >= 1 and <= 16), "groups hold 1..16 bytes");
        Check.True(CdCarrierGeometry.GroupSizes.ToArray().Average(size => (double)size) > 12, "windows are mostly full of payload");
        int span = CdCarrierGeometry.SlotSector.ToArray().Max() + 1;
        Check.Equal(CdCarrierGeometry.SectorSpan, span, "a frame reads exactly 18 sectors");

        // The whole premise: the layout repeats unchanged every 16 sectors.
        for (int sector = 0; sector < 40; sector++)
        {
            for (int offset = 0; offset < SectorBytes; offset += 61)
            {
                long near = CdDataImageGenerator.PhysicalByteForPayload(100 + sector, offset);
                long far = CdDataImageGenerator.PhysicalByteForPayload(100 + sector + 16 * 977, offset);
                Check.Equal(near + (16L * 977 * CdCarrierGeometry.RawBytesPerSector), far, "the layout is translation invariant");
            }
        }

        // Slots and the generator agree on where a payload byte lives.
        long frame = 77;
        for (int slot = 0; slot < CdCarrierGeometry.SlotsPerFrame; slot += 97)
        {
            long lba = CdCarrierGeometry.FirstSector(frame) + CdCarrierGeometry.SlotSector[slot];
            Check.Equal(CdDataImageGenerator.PhysicalByteForPayload(lba, CdCarrierGeometry.SlotOffset[slot]),
                CdCarrierGeometry.PhysicalByte(frame, slot), "slot position equals the generator's position");
        }

        Check.True(CdCarrierGeometry.IsEligible(10, 160, 1000) && !CdCarrierGeometry.IsEligible(9, 160, 1000),
            "frames before the drawing are not eligible");
        Check.Equal(10L, CdCarrierGeometry.FirstEligibleFrame(160), "first eligible frame is aligned");
        Check.Equal(11L, CdCarrierGeometry.FirstEligibleFrame(161), "an unaligned start rounds up");
        long end = CdCarrierGeometry.EndEligibleFrame(160, 1000);
        Check.True(CdCarrierGeometry.IsEligible(end - 1, 160, 1000) && !CdCarrierGeometry.IsEligible(end, 160, 1000),
            "the last eligible frame ends inside the drawing");
        Check.Equal(10L, CdCarrierGeometry.EndEligibleFrame(160, 170), "a drawing shorter than one frame has none");
    }

    /// <summary>Palette bytes as the generator would hand them to the stream, scrambled.</summary>
    private static byte[] SyntheticDrawing(Random random, long sectors, long drawingStart, long drawingEnd)
    {
        byte[] image = new byte[sectors * SectorBytes];
        random.NextBytes(image); // filesystem-like data outside the drawing
        for (long lba = drawingStart; lba < drawingEnd; lba++)
        {
            double density = 0.2 + (0.6 * ((lba % 300) / 300.0));
            for (int offset = 0; offset < SectorBytes; offset++)
            {
                byte palette = random.NextDouble() < density ? (byte)0xAA : (byte)0x0D;
                image[(lba * SectorBytes) + offset] = (byte)(palette ^ CdDataImageGenerator.PayloadMask(offset));
            }
        }

        return image;
    }

    private static byte[] Filter(byte[] image, ICarrierBitSource source, long start, long end, int chunk, out CarrierStats stats)
    {
        using MemoryStream output = new();
        CdBitCarrier carrier = new(source);
        CdBitCarrierStream stream = carrier.Wrap(output, start, end);
        for (int at = 0; at < image.Length; at += chunk)
            stream.Write(image, at, Math.Min(chunk, image.Length - at));
        stream.Complete();
        stats = carrier.Stats;
        return output.ToArray();
    }

    private static long LightCount(byte[] image, long frame, int slotFrom, int slotTo)
    {
        long count = 0;
        for (int slot = slotFrom; slot < slotTo; slot++)
        {
            long lba = CdCarrierGeometry.FirstSector(frame) + CdCarrierGeometry.SlotSector[slot];
            int offset = CdCarrierGeometry.SlotOffset[slot];
            if ((byte)(image[(lba * SectorBytes) + offset] ^ CdDataImageGenerator.PayloadMask(offset)) == 0xAA)
                count++;
        }

        return count;
    }

    private static void AssertDrawingPreserved(byte[] before, byte[] after, long start, long end, string context)
    {
        Check.Equal(before.Length, after.Length, context + ": length");
        long firstFrame = CdCarrierGeometry.FirstEligibleFrame(start);
        long endFrame = CdCarrierGeometry.EndEligibleFrame(start, end);
        // Bytes of sectors that no eligible frame reads must be identical.
        HashSet<long> touched = [];
        for (long frame = firstFrame; frame < endFrame; frame++)
            for (int sector = 0; sector < CdCarrierGeometry.SectorSpan; sector++)
                touched.Add(CdCarrierGeometry.FirstSector(frame) + sector);
        for (long lba = 0; lba < before.Length / SectorBytes; lba++)
        {
            if (!touched.Contains(lba))
            {
                Check.True(before.AsSpan((int)(lba * SectorBytes), SectorBytes).SequenceEqual(after.AsSpan((int)(lba * SectorBytes), SectorBytes)),
                    $"{context}: sector {lba} is outside every eligible frame and must be untouched");
            }
        }

        // In every eligible frame the number of light bytes per physical group is unchanged.
        bool changed = false;
        ReadOnlySpan<byte> sizes = CdCarrierGeometry.GroupSizes;
        for (long frame = firstFrame; frame < endFrame; frame++)
        {
            int slot = 0;
            foreach (byte size in sizes)
            {
                Check.Equal(LightCount(before, frame, slot, slot + size), LightCount(after, frame, slot, slot + size),
                    $"{context}: light bytes of a physical group in frame {frame}");
                slot += size;
            }

            for (int index = 0; index < CdCarrierGeometry.SlotsPerFrame && !changed; index += 7)
            {
                long lba = CdCarrierGeometry.FirstSector(frame) + CdCarrierGeometry.SlotSector[index];
                int offset = CdCarrierGeometry.SlotOffset[index];
                changed |= before[(lba * SectorBytes) + offset] != after[(lba * SectorBytes) + offset];
            }
        }

        Check.True(changed, context + ": the arrangement was actually changed");
        // Only the two palette values may appear in the drawing.
        for (long frame = firstFrame; frame < endFrame; frame++)
            for (int slot = 0; slot < CdCarrierGeometry.SlotsPerFrame; slot += 13)
            {
                long lba = CdCarrierGeometry.FirstSector(frame) + CdCarrierGeometry.SlotSector[slot];
                int offset = CdCarrierGeometry.SlotOffset[slot];
                byte palette = (byte)(after[(lba * SectorBytes) + offset] ^ CdDataImageGenerator.PayloadMask(offset));
                Check.True(palette is 0x0D or 0xAA, context + ": only palette bytes");
            }
    }

    private static void TestStreamFilterOnSyntheticSectors()
    {
        const long sectors = 700, start = 37, end = 640;
        Random random = new(4242);
        byte[] image = SyntheticDrawing(random, sectors, start, end);
        byte[] data = new byte[60_000];
        random.NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        byte[] embedded = Filter(image, payload, start, end, chunk: 2048, out CarrierStats stats);
        Check.True(stats.Complete, "the synthetic drawing holds the payload");
        AssertDrawingPreserved(image, embedded, start, end, "synthetic");

        // How the generator happens to chunk its writes must not matter.
        using CarrierPayload again = CarrierPayload.FromBytes(data);
        Check.True(Filter(image, again, start, end, chunk: 997, out _).AsSpan().SequenceEqual(embedded),
            "the output does not depend on the write sizes");

        using MemoryStream recovered = new();
        CarrierExtractResult result = CdBitCarrierExtractor.Extract(new MemoryStream(embedded), recovered);
        Check.True(result.Complete && recovered.ToArray().AsSpan().SequenceEqual(data), "extraction returns the file");
        using MemoryStream none = new();
        Check.True(!CdBitCarrierExtractor.Extract(new MemoryStream(image), none).HeaderFound,
            "an image without a carrier contains no frames");

        // A drawing too short for one frame (a frame reads 18 sectors) is passed through unchanged.
        using CarrierPayload tiny = CarrierPayload.FromBytes([1, 2, 3]);
        byte[] shortImage = SyntheticDrawing(new Random(1), 40, 0, 17);
        Check.True(Filter(shortImage, tiny, 0, 17, 2048, out CarrierStats none2).AsSpan().SequenceEqual(shortImage)
            && none2.BlocksProcessed == 0, "a drawing shorter than a frame is untouched");

        // Destroy the sectors a middle frame reads: only data from the frames around it is lost.
        byte[] damaged = (byte[])embedded.Clone();
        long victim = 16 * 12;
        new Random(8).NextBytes(damaged.AsSpan((int)(victim * SectorBytes), SectorBytes));
        using MemoryStream partial = new();
        CarrierExtractResult loss = CdBitCarrierExtractor.Extract(new MemoryStream(damaged), partial);
        Check.True(loss.HeaderFound && !loss.Complete && loss.MissingBits > 0, "a damaged sector is reported");
        Check.True(loss.MissingBits < 150_000, "the loss is confined to the frames that read that sector");
        byte[] rest = partial.ToArray();
        Check.True(rest.AsSpan(0, 800).SequenceEqual(data.AsSpan(0, 800)), "data before the damage survives");
        Check.True(rest.AsSpan(rest.Length - 800).SequenceEqual(data.AsSpan(data.Length - 800)), "data after the damage survives");
    }

    private static void WriteImage(string path, int size)
    {
        byte[] pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double radius = Math.Sqrt(Math.Pow(x - size / 2.0, 2) + Math.Pow(y - size / 2.0, 2)) / (size / 2.0);
                byte gray = (byte)Math.Clamp(255 * (1 - (radius * 0.9)) + (30 * Math.Sin(x / 17.0)), 0, 255);
                int at = ((y * size) + x) * 4;
                pixels[at] = pixels[at + 1] = pixels[at + 2] = gray;
                pixels[at + 3] = 255;
            }

        RasterCodec.SavePng(path, size, size, pixels, grayscale: false);
    }

    private static (string Image, string Files) Prepare(string directory)
    {
        string image = Path.Combine(directory, "picture.png");
        WriteImage(image, 256);
        string files = Path.Combine(directory, "files");
        Directory.CreateDirectory(files);
        byte[] blob = new byte[150_000];
        new Random(3).NextBytes(blob);
        File.WriteAllBytes(Path.Combine(files, "data.bin"), blob);
        return (image, files);
    }

    private static CdDiscParameters Parameters(long sectors) => new(24.5, 56.8, sectors);

    private static void TestGenerationInnerAndOuterFiles(string directory)
    {
        (string image, string files) = Prepare(directory);
        ToneProfile tone = new(0.75, ToneProfile.TwoLevels, 0, true);
        byte[] data = new byte[40_000];
        new Random(21).NextBytes(data);
        foreach (bool outer in new[] { false, true })
        {
            string label = outer ? "outer ring files" : "inner ring files";
            CdDiscParameters parameters = Parameters(6000);
            CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, parameters, "CARRIER", outer);
            string basePath = Path.Combine(directory, $"base-{outer}.iso");
            string embeddedPath = Path.Combine(directory, $"embedded-{outer}.iso");
            CdDataImageGenerator.Generate(image, basePath, parameters, plan, tone: tone);
            using CarrierPayload payload = CarrierPayload.FromBytes(data);
            CdBitCarrier carrier = new(payload);
            CdDataImageGenerator.Generate(image, embeddedPath, parameters, plan, tone: tone, carrier: carrier);
            Check.True(carrier.Stats.Complete, label + ": the drawing holds the payload");

            byte[] before = File.ReadAllBytes(basePath);
            byte[] after = File.ReadAllBytes(embeddedPath);
            AssertDrawingPreserved(before, after, plan.DrawingStartLba, plan.DrawingEndLbaExclusive, label);

            // The filesystem is byte-identical (the generator's own ISO writer is deterministic for one plan).
            Check.True(before.AsSpan(0, (int)(plan.DrawingStartLba * SectorBytes)).SequenceEqual(after.AsSpan(0, (int)(plan.DrawingStartLba * SectorBytes))),
                label + ": filesystem sectors before the drawing are untouched");
            if (outer)
            {
                int tail = (int)(plan.DrawingEndLbaExclusive * SectorBytes);
                Check.True(before.AsSpan(tail).SequenceEqual(after.AsSpan(tail)), label + ": outer files are untouched");
            }

            using MemoryStream output = new();
            CarrierExtractResult result = CdBitCarrierExtractor.Extract(new MemoryStream(after), output);
            Check.True(result.Complete && output.ToArray().AsSpan().SequenceEqual(data), label + ": the file comes back");
        }
    }

    private static void TestCapacityIsExact(string directory)
    {
        (string image, string files) = Prepare(directory);
        ToneProfile tone = new(0.75, ToneProfile.TwoLevels, 0, true);
        CdDiscParameters parameters = Parameters(6000);
        CdDataLayoutPlan plan = CdDataImageGenerator.PrepareLayout(files, parameters, "CARRIER", false);
        List<CdProgress> updates = [];
        CarrierCapacity capacity = CdDataImageGenerator.MeasureCarrierCapacity(image, parameters, plan, tone, updates.Add);
        Check.True(updates.Count > 0 && updates[^1].Fraction >= 0.999, "progress reaches the end");
        Check.True(capacity.MaxFileBytes > 1000 && capacity.BitsPerByte > 0.05, "a midtone picture has real capacity");

        byte[] data = new byte[capacity.MaxFileBytes + 1];
        new Random(5).NextBytes(data);
        for (long size = capacity.MaxFileBytes; size <= capacity.MaxFileBytes + 1; size++)
        {
            using MemoryStream part = new(data, 0, (int)size, writable: false);
            using CarrierPayload payload = new(part);
            CdBitCarrier carrier = new(payload);
            string iso = Path.Combine(directory, $"cap-{size}.iso");
            CdDataImageGenerator.Generate(image, iso, parameters, plan, tone: tone, carrier: carrier);
            CarrierStats stats = carrier.Stats;
            Check.Equal(capacity.RawBits, stats.CapacityBits, "embedding sees the capacity that was measured");
            Check.Equal(capacity.DataBits, stats.UsableDataBits, "usable bits agree with the measurement");
            Check.Equal(capacity.MaxFileBytes, stats.MaxFileBytes, "max file size agrees");
            bool fits = size == capacity.MaxFileBytes;
            Check.Equal(fits, stats.Complete, fits ? "a file of exactly the measured size is stored completely"
                : "one byte more than the measured size cannot be stored");
            if (fits)
            {
                using MemoryStream output = new();
                Check.True(CdBitCarrierExtractor.Extract(File.OpenRead(iso), output).Complete
                    && output.ToArray().AsSpan().SequenceEqual(data.AsSpan(0, (int)size)), "and it comes back intact");
            }
        }
    }
}
