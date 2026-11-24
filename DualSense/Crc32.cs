using System.Buffers.Binary;

namespace GripLink.DualSense;

/// <summary>
/// Standard CRC-32 (IEEE 802.3, reflected, poly 0xEDB88320).
/// Over Bluetooth every DualSense report ends with a CRC-32 computed over a
/// one-byte "seed" (the BT HIDP header) followed by the report bytes.
/// </summary>
public static class Crc32
{
    public const byte InputSeed = 0xA1;   // HIDP DATA | INPUT
    public const byte OutputSeed = 0xA2;  // HIDP DATA | OUTPUT
    public const byte FeatureSeed = 0xA3; // HIDP DATA | FEATURE

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static uint Step(uint crc, byte b) => Table[(crc ^ b) & 0xFF] ^ (crc >> 8);

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data) crc = Step(crc, b);
        return ~crc;
    }

    public static uint Compute(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = Step(0xFFFFFFFF, seed);
        foreach (var b in data) crc = Step(crc, b);
        return ~crc;
    }

    /// <summary>Writes the CRC of report[0..^4] into the last 4 bytes (little endian).</summary>
    public static void Seal(byte seed, Span<byte> report)
    {
        uint crc = Compute(seed, report[..^4]);
        BinaryPrimitives.WriteUInt32LittleEndian(report[^4..], crc);
    }

    public static bool Verify(byte seed, ReadOnlySpan<byte> report)
    {
        if (report.Length < 5) return false;
        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(report[^4..]);
        return Compute(seed, report[..^4]) == expected;
    }
}
