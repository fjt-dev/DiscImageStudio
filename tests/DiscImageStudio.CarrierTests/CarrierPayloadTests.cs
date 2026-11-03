using DiscImageStudio.Core.Carrier;

internal static class CarrierPayloadTests
{
    internal static void Run()
    {
        Random random = new(5);
        byte[] data = new byte[300];
        random.NextBytes(data);
        using CarrierPayload payload = CarrierPayload.FromBytes(data);
        byte[] container = new byte[CarrierContainer.HeaderBytes + data.Length];
        CarrierContainer.BuildHeader(data.Length, CarrierCrc32.Compute(data)).CopyTo(container, 0);
        data.CopyTo(container, CarrierContainer.HeaderBytes);
        Check.Equal((ulong)container.Length * 8, payload.TotalBits, "total bits");
        Check.True(CarrierContainer.TryParseHeader(container, out long length, out uint crc)
            && length == data.Length && crc == CarrierCrc32.Compute(data), "header parses back");
        Check.Equal(0xCBF43926u, CarrierCrc32.Compute("123456789"u8), "CRC-32 check value");

        for (int trial = 0; trial < 2000; trial++)
        {
            ulong offset = (ulong)random.Next(0, container.Length * 8 + 40);
            int count = random.Next(0, 700);
            byte[] actual = new byte[(count + 7) / 8 + 1];
            actual[^1] = 0xAA; // sentinel beyond the requested bits must survive
            payload.ReadBits(offset, count, actual);
            for (int bit = 0; bit < count; bit++)
            {
                ulong source = offset + (ulong)bit;
                int expected = source < (ulong)container.Length * 8
                    ? (container[(int)(source / 8)] >> (7 - (int)(source % 8))) & 1
                    : 0;
                int got = (actual[bit / 8] >> (7 - bit % 8)) & 1;
                Check.Equal(expected, got, $"bit {bit} of read at {offset}");
            }

            Check.Equal((byte)0xAA, actual[^1], "bytes beyond the request are not touched");
            if (count % 8 != 0)
                Check.Equal(0, actual[count / 8] & (0xFF >> (count % 8)), "unused tail bits are zero");
        }

        Console.WriteLine("carrier-payload: passed (container header, CRC-32 check value, 2000 random bit-range reads)");
    }
}
