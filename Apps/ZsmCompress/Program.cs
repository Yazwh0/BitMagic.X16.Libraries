namespace ZsmCompress;

class Program
{
    static void Main()
    {
        // Try to load example file first; fall back to demo-generated blocks if not present or on error.
        var blocks = new List<ZsmBlock>();
        const string samplePath = @"C:\Documents\Source\Bitmap\LibraryDev\ZsmPlayer\AUDIO.BIN";

        if (File.Exists(samplePath))
        {
            try
            {
                var parser = new ZsmParser(1, false);
                var (header, parsedBlocks) = parser.ParseFile(samplePath);
                blocks = parsedBlocks ?? new List<ZsmBlock>();
                Console.WriteLine($"Loaded {blocks.Count} blocks from '{samplePath}' (version {header.Version}). Total Size {blocks.Sum(i => i.Length)} bytes");

                Dictionary<string, (int Count, int Index, int Address)> hashCounts = new();
                var hashSize = 0;
                List<int> zsmBlocks = new();

                int address = 0x02a000;

                address = AddAddress(address, blocks.Count * 3); // move the address on away from the pointers

                foreach (var i in blocks)
                {
                    if (hashCounts.ContainsKey(i.DataHashHex))
                    {
                        var hashCount = hashCounts[i.DataHashHex];
                        zsmBlocks.Add(hashCount.Address);

                        hashCount.Count++;
                        hashCounts[i.DataHashHex] = hashCount;
                    }
                    else
                    {
                        i.Address = address;
                        zsmBlocks.Add(address);

                        hashCounts[i.DataHashHex] = (1, hashCounts.Count, address);
                        hashSize += i.Data.Length;

                        address = AddAddress(address, i.Length);
                    }
                }

                foreach(var kv in hashCounts.OrderByDescending(kv => kv.Value.Count))
                {
                    Console.WriteLine($"Hash {kv.Key} occurs {kv.Value.Count} times.");
                }

                var total = blocks.Sum(i => i.Length);
                var dictSize = blocks.Count * 3;

                Console.WriteLine($"Total Size : {total} bytes");
                Console.WriteLine($"Total Unique Size : {hashSize} bytes, Dictionary Size : {dictSize} bytes, Total {hashSize + dictSize} ({(hashSize + dictSize) / (double)total:0.00%})");

                SaveZsmDictionary(@"C:\Documents\Source\Bitmap\LibraryDev\ZsmPlayer\AUDCOMP.BIN", zsmBlocks, hashCounts, blocks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load '{samplePath}': {ex.Message}. Falling back to demo data.");
            }
        }
        else
        {
            Console.WriteLine($"Example file '{samplePath}' not found. Using demo-generated blocks.");
        }
    }

    static int AddAddress(int address, int length)
    {
        var bank = (address & 0xFF0000) >> 16;
        var rawAddress = address & 0x00FFFF;

        rawAddress -= 0xa000;

        rawAddress += length;
        while(rawAddress > 0x2000)
        {
            rawAddress -= 0x2000;
            bank++;
        }

        rawAddress += 0xa000;

        return (bank << 16) | rawAddress;
    }


    // Writes the pointer table followed by the unique blocks in dictionary order.
    // - Each entry in `zsmBlocks` is written as 3 bytes little-endian (low, mid, high).
    // - Unique blocks are written in ascending Index order from `hashCounts`.
    static void SaveZsmDictionary(string outputPath, List<int> zsmBlocks, Dictionary<string, (int Count, int Index, int Address)> hashCounts, List<ZsmBlock> blocks)
    {
        if (outputPath is null) throw new ArgumentNullException(nameof(outputPath));
        if (zsmBlocks is null) throw new ArgumentNullException(nameof(zsmBlocks));
        if (hashCounts is null) throw new ArgumentNullException(nameof(hashCounts));
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));

        // Ensure directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);

        // Write pointer table: each pointer as 3 bytes little-endian
        foreach (var ptr in zsmBlocks)
        {
            var value = ptr - 1;
            fs.WriteByte((byte)(value & 0xFF));
            fs.WriteByte((byte)((value >> 8) & 0xFF));
            fs.WriteByte((byte)((value >> 16) & 0xFF));
        }

        // Write unique blocks in order of their Index value (ascending)
        foreach (var kv in hashCounts.OrderBy(kv => kv.Value.Index))
        {
            var address = kv.Value.Address;

            // Prefer finding block by Address (unique blocks had Address assigned).
            var block = blocks.FirstOrDefault(b => b.Address == address);

            // Fall back to matching by hash if address-matching fails.
            if (block is null)
            {
                throw new Exception();
                block = blocks.FirstOrDefault(b => b.DataHashHex == kv.Key);
            }

            if (block is null)
            {
                throw new Exception();
                // Missing block for this entry; skip gracefully
                continue;
            }

            fs.Write(block.Data, 0, block.Data.Length);
        }

        fs.Flush();
    }
}
