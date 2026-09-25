namespace RAF.Core.FileSystems.Btrfs;

/// <summary>CRC32C (Castagnoli) — טביעת האצבע שבראש כל בלוק של btrfs.</summary>
internal static class Crc32C
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    internal static uint Compute(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);

    /// <summary>המשך חישוב בלי היפוך בסוף — כך ext4 מחשב את טביעות האצבע שלו.</summary>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
