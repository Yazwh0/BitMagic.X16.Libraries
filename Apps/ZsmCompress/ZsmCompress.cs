using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
// Needed for calling from BMASM
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace ZsmCompress;

public static class ZsmCompressor
{
    // output is

    public static byte[] Compress(byte[] inputData, int bank, int address, out int dictionarySize, out int dataSize, out int pcmSize, out string procs)
    {
        var parser = new ZsmParser(1, false);
        // ParseStream now returns PCM instruments and raw PCM bytes; preserve previous behavior by ignoring instruments but pass them to CreatZsmComp.
        var (_, parsedBlocks, parsedPcmContainer, parsedPcmData) = parser.ParseStream(new MemoryStream(inputData));
        var blocks = parsedBlocks ?? new List<ZsmBlock>();

        var hashCounts = new Dictionary<string, (int Count, int Index, int Address)> ();
        var hashSize = 0;
        var zsmBlocks = new List<int>();

        address = (bank << 16) + (address & 0xffff); // 0x02a000;

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

        dictionarySize = blocks.Count * 3;
        dataSize = hashSize;

        // 'address' now points to the banked address immediately after the unique block data.
        // Pass parsed instruments + pcm blob + the start address so CreatZsmComp can append each instrument padded to 16 bytes
        // and set its Address field to the correct banked address.
        return CreatZsmComp(zsmBlocks, hashCounts, blocks, parsedPcmContainer, parsedPcmData, address, out pcmSize, out procs);
    }

    private static int AddAddress(int address, int length)
    {
        var bank = (address & 0xFF0000) >> 16;
        var rawAddress = address & 0x00FFFF;

        if (rawAddress < 0xa000)
            throw new Exception();

        rawAddress -= 0xa000;
        rawAddress += length;

        while (rawAddress > 0x2000)
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
    // - If `pcmContainer` is provided, each instrument's PCM data is appended after the block data.
    //   Each instrument is individually padded so it starts on a 16-byte boundary. The instrument's
    //   `Address` property is set to the banked address where its sample data begins.
    // - If no `pcmContainer` but `pcmData` is present, the entire blob is appended and aligned to 16 bytes.
    // Returns the generated output and sets out `pcmSize` to the total size of appended PCM data including padding.
    private static byte[] CreatZsmComp(
        List<int> zsmBlocks,
        Dictionary<string, (int Count, int Index, int Address)> hashCounts,
        List<ZsmBlock> blocks,
        PcmContainer? pcmContainer,
        byte[]? pcmData,
        int pcmStartAddress,
        out int pcmSize,
        out string procs)
    {
        if (zsmBlocks is null) throw new ArgumentNullException(nameof(zsmBlocks));
        if (hashCounts is null) throw new ArgumentNullException(nameof(hashCounts));
        if (blocks is null) throw new ArgumentNullException(nameof(blocks));

        using var outputStream = new MemoryStream();
        pcmSize = 0;
        procs = "";

        // Write pointer table: each pointer as 3 bytes little-endian
        foreach (var ptr in zsmBlocks)
        {
            var value = ptr - 1;
            outputStream.WriteByte((byte)(value & 0xFF));
            outputStream.WriteByte((byte)((value >> 8) & 0xFF));
            outputStream.WriteByte((byte)((value >> 16) & 0xFF));
        }

        // Write unique blocks in order of their Index value (ascending)
        foreach (var kv in hashCounts.OrderBy(kv => kv.Value.Index))
        {
            var address = kv.Value.Address;

            // Prefer finding block by Address (unique blocks had Address assigned).
            var block = blocks.First(b => b.Address == address);

            outputStream.Write(block.Data, 0, block.Data.Length);
        }

        // If we have a PCM container with per-instrument data, append each instrument padded to 16 bytes
        if (pcmContainer is not null && pcmContainer.Instruments is { Count: > 0 })
        {
            // Start with the banked address that corresponds to the current output position
            int currBankedAddress = pcmStartAddress;

            var sb = new StringBuilder();

            // Ensure instruments are appended in index order for deterministic layout
            foreach (var inst in pcmContainer.Instruments.OrderBy(i => i.Index))
            {
                // Pad output to 16-byte boundary before this instrument
                long mod = outputStream.Length % 16;
                int pad = (int)((16 - mod) % 16);
                if (pad > 0)
                {
                    var padBytes = new byte[pad];
                    outputStream.Write(padBytes, 0, pad);
                    pcmSize += pad;
                    currBankedAddress = AddAddress(currBankedAddress, pad);
                }

                // Record instrument address (banked)
                inst.Address = currBankedAddress;

                // Append instrument data
                if (inst.Data is not null && inst.Data.Length > 0)
                {
                    outputStream.Write(inst.Data, 0, inst.Data.Length);
                    pcmSize += inst.Data.Length;
                    currBankedAddress = AddAddress(currBankedAddress, inst.Data.Length);
                }

                sb.AppendLine(@$"
.proc play_instrument_{inst.Index}

    lda #$00 ; initial length for start             MAKE THIS CALL NOW??
    sta tick:initial_playback_part_length

    lda #${(byte)(inst.Address & 0xff):X2} ; sample start l
    sta sample_pointer

    lda #${(byte)((inst.Address >> 8) & 0xff):X2} ; sample start m
    sta sample_pointer + 1

    lda #${(byte)((inst.Address >> 16) & 0xff):X2} ; sample start h
    sta sample_pointer_bank

    lda #$00 ; length per frame in 16 byte blocks
    sta pcm_counter

    lda #$00 ; frames
    sta $0000

    lda #$00 ; last frame count in 16 byte blocks
    sta $0000

    lda #{(inst.IsLooped ? 1 : 0)} ; has repeater
    sta $0000

    lda #$00 ; loop start l
    sta $0000
    lda #$00  ; loop start m    sta sample_pointer
    sta $0000
    lda #$00  ; loop start h
    sta $0000

    rts

.endproc
");

            }

            procs = sb.ToString();
        }
        else if (pcmData is not null && pcmData.Length > 0)
        {
            // Fallback: append the whole blob aligned to 16 bytes
            long mod = outputStream.Length % 16;
            int pad = (int)((16 - mod) % 16);
            if (pad > 0)
            {
                var padBytes = new byte[pad];
                outputStream.Write(padBytes, 0, pad);
                pcmSize += pad;
            }

            outputStream.Write(pcmData, 0, pcmData.Length);
            pcmSize += pcmData.Length;
        }
        else
        {
            // no PCM appended; pcmSize remains 0
        }


        return outputStream.ToArray();
    }
}

internal sealed record ZsmHeader(
    byte Version,
    int LoopPoint,       // 24-bit little-endian
    int PcmOffset,       // 24-bit little-endian (0 = no PCM)
    byte FmChannelMask,
    ushort PsgChannelMask,
    ushort TickRate
);

[DebuggerDisplay("Length={Length} Hash={DataHashHex,nq}")]
internal sealed record ZsmBlock(
    long Offset,     // file offset where block starts (relative to file start)
    int Length,      // length in bytes
    byte[] Data,     // raw bytes of the music stream for this block (includes trailing pause/end marker)
    bool EndsWithPause,
    int PauseTicks   // 0 if not a Delay pause; >0 if block ends with a Delay tick count; -1 if ended with EOF (0x80)
)
{
    // SHA-256 hash of `Data`. Computed lazily and cached on first access.
    private byte[]? _dataHash;
    public byte[] DataHash => _dataHash ??= ComputeSha256Hash(Data);
    public string DataHashHex => DataHash.Length == 0 ? string.Empty : BitConverter.ToString(DataHash).Replace("-", "").ToLowerInvariant();

    public int Address { get; set; } = 0;

    private static byte[] ComputeSha256Hash(byte[]? data)
    {
        if (data is null || data.Length == 0) return Array.Empty<byte>();
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(data);
    }
}

/// <summary>
/// PCM instrument description parsed from the PCM header.
/// </summary>
internal sealed record PcmInstrument(
    byte Index,
    bool Is16Bit,
    bool IsStereo,
    int Offset,     // offset into PCM data blob (relative to PCM sample data start)
    int Length,     // length in bytes
    bool IsLooped,
    int LoopPoint,  // offset into this instrument's sample (relative)
    byte[] Data     // the actual PCM bytes for this instrument
)
{
    // Filled by CreatZsmComp when the PCM blob is appended.
    public int Address { get; set; } = 0;
}

internal sealed record PcmContainer(
    byte LastIndex,
    List<PcmInstrument> Instruments
);

/// <summary>
/// Parses a ZSM file and splits the music stream into blocks that end in a "pause".
/// A "pause" here is a Delay command (0x81-0xFF) whose ticks >= minPauseTicks, or EOF (0x80).
/// The parser consumes the ZSM header and the music stream up to the 0x80 marker. PCM header/data after 0x80
/// are parsed according to the Furnace / X16 ZSM spec: instruments are returned as structured objects and the PCM
/// sample data blob is returned as raw bytes as well.
///
/// New: The parser can optionally exclude EXTCMD blocks (0x40 and following bytes) from the returned block data
/// by setting includeExtCmds = false. The parser will still consume those bytes from the stream (advancing offsets),
/// but they will not be written into the block byte arrays.
/// </summary>
internal sealed class ZsmParser
{
    private readonly int _minPauseTicks;
    private readonly bool _includeExtCmds;

    /// <summary>
    /// Create a parser.
    /// minPauseTicks: a Delay command with ticks >= this value is considered a pause (block boundary). Default = 1.
    /// includeExtCmds: if false, EXTCMD blocks (0x40 + ext header + ext bytes) are consumed but not included in block data. Default = true.
    /// </summary>
    public ZsmParser(int minPauseTicks = 1, bool includeExtCmds = true)
    {
        if (minPauseTicks < 1) throw new ArgumentOutOfRangeException(nameof(minPauseTicks));
        _minPauseTicks = minPauseTicks;
        _includeExtCmds = includeExtCmds;
    }

    public (ZsmHeader Header, List<ZsmBlock> Blocks, PcmContainer? Instruments, byte[]? PcmData) ParseFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ParseStream(fs);
    }

    public (ZsmHeader Header, List<ZsmBlock> Blocks, PcmContainer? Instruments, byte[]? PcmData) ParseStream(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead) throw new ArgumentException("Stream must be readable", nameof(stream));

        // track absolute read offset manually (works for non-seekable streams)
        long absOffset = 0;
        byte[] tmp = new byte[16];
        ReadExactly(stream, tmp, 0, 16, ref absOffset);

        // Validate header magic "zm" (0x7A 0x6D)
        if (tmp[0] != 0x7A || tmp[1] != 0x6D) throw new InvalidDataException("Not a ZSM file (missing 'zm' magic)");

        byte version = tmp[2];
        int loopPoint = ReadLe24(tmp, 3);
        int pcmOffset = ReadLe24(tmp, 6);
        byte fmMask = tmp[9];
        ushort psgMask = (ushort)(tmp[0x0A] | (tmp[0x0B] << 8));
        ushort tickRate = (ushort)(tmp[0x0C] | (tmp[0x0D] << 8));
        // reserved bytes at 0x0E-0x0F ignored

        var header = new ZsmHeader(version, loopPoint, pcmOffset, fmMask, psgMask, tickRate);

        var blocks = new List<ZsmBlock>();
        using var current = new MemoryStream();

        long blockStartOffset = absOffset; // music stream begins immediately after header (offset 16)
        int b;
        PcmContainer? pcmContainer = null;
        byte[]? pcmData = null;

        while (true)
        {
            b = ReadByteOrThrow(stream, ref absOffset);

            if (b >= 0x00 && b <= 0x3F)
            {
                // PSG write: 1 data byte follows
                // include opcode and data byte
                current.WriteByte((byte)b);
                int d = ReadByteOrThrow(stream, ref absOffset);
                current.WriteByte((byte)d);
                continue;
            }
            else if (b == 0x40)
            {
                // EXTCMD marker: next byte = ccnnnnnn, n = count of ext bytes
                int extHdr = ReadByteOrThrow(stream, ref absOffset);
                int n = extHdr & 0x3F;
                if (_includeExtCmds)
                {
                    // include opcode, ext header and ext bytes
                    current.WriteByte((byte)b);
                    current.WriteByte((byte)extHdr);
                    if (n > 0) ReadAndWrite(stream, current, n, ref absOffset);
                }
                else
                {
                    // consume ext bytes but do not include them in block data
                    if (n > 0) ReadAndDiscard(stream, n, ref absOffset);
                }
                continue;
            }
            else if (b >= 0x41 && b <= 0x7F)
            {
                // FM write: lower 6 bits = n, followed by 2*n bytes (reg/value pairs)
                int n = b & 0x3F;
                int bytesToRead = 2 * n;
                // include opcode and following register/value bytes
                current.WriteByte((byte)b);
                if (bytesToRead > 0) ReadAndWrite(stream, current, bytesToRead, ref absOffset);
                continue;
            }
            else if (b == 0x80)
            {
                // EOF music stream marker - treat as pause terminator; finalize current block (if any)
                // include the 0x80 byte
                current.WriteByte((byte)b);
                FinalizeCurrentBlock(blocks, current, blockStartOffset, endsWithPause: true, pauseTicks: -1);

                // Attempt to parse PCM header/data according to spec if present.
                if (header.PcmOffset != 0)
                {
                    long pcmHeaderAbs = header.PcmOffset; // absolute offset from start of ZSM header (file)
                    // Try to position to pcmHeaderAbs
                    if (stream.CanSeek)
                    {
                        stream.Position = pcmHeaderAbs;
                        absOffset = pcmHeaderAbs;
                    }
                    else
                    {
                        if (pcmHeaderAbs > absOffset)
                        {
                            // read and discard until we reach pcmHeaderAbs
                            ReadAndDiscard(stream, (int)(pcmHeaderAbs - absOffset), ref absOffset);
                        }
                        else if (pcmHeaderAbs < absOffset)
                        {
                            // cannot seek backwards on non-seekable stream; we'll attempt to parse at current position
                            // but prefer to fail-safe by checking magic before trusting header.
                        }
                    }

                    // Read 4 bytes for PCM header signature + last index
                    var phdr = new byte[4];
                    try
                    {
                        ReadExactly(stream, phdr, 0, 4, ref absOffset);
                    }
                    catch (EndOfStreamException)
                    {
                        // no PCM header present
                        break;
                    }

                    if (phdr[0] == (byte)'P' && phdr[1] == (byte)'C' && phdr[2] == (byte)'M')
                    {
                        byte lastIndex = phdr[3];
                        int instrumentCount = lastIndex + 1;
                        int pcmHeaderSize = 4 + (16 * instrumentCount);

                        // we already read 4 bytes; read the remainder of the header
                        var fullHeader = new byte[pcmHeaderSize];
                        Array.Copy(phdr, 0, fullHeader, 0, 4);
                        if (pcmHeaderSize > 4)
                        {
                            ReadExactly(stream, fullHeader, 4, pcmHeaderSize - 4, ref absOffset);
                        }

                        // Calculate sample data start (absolute)
                        long sampleDataAbs = (stream.CanSeek ? stream.Position : absOffset);

                        // Read the remaining bytes as PCM sample blob
                        using var pcmStream = new MemoryStream();
                        var buf = new byte[4096];
                        int r;
                        while ((r = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            pcmStream.Write(buf, 0, r);
                            absOffset += r;
                        }

                        pcmData = pcmStream.Length == 0 ? Array.Empty<byte>() : pcmStream.ToArray();

                        // Parse instrument entries
                        var instruments = new List<PcmInstrument>(instrumentCount);
                        for (int i = 0; i < instrumentCount; i++)
                        {
                            int baseOff = 4 + (i * 16);
                            byte instIndex = fullHeader[baseOff + 0];
                            byte audioCtrl = fullHeader[baseOff + 1];
                            int off24 = ReadLe24(fullHeader, baseOff + 2);   // offset into pcm data blob
                            int len24 = ReadLe24(fullHeader, baseOff + 5);   // length
                            byte features = fullHeader[baseOff + 8];
                            int loopPt = ReadLe24(fullHeader, baseOff + 9);

                            bool is16 = (audioCtrl & 0x20) != 0;
                            bool isStereo = (audioCtrl & 0x10) != 0;
                            bool isLooped = (features & 0x80) != 0;

                            // Validate bounds of offset/length inside pcmData
                            if (off24 < 0 || len24 < 0 || off24 + len24 > (pcmData?.Length ?? 0))
                            {
                                // Invalid instrument spec — throw to indicate malformed PCM header.
                                throw new InvalidDataException($"PCM instrument {instIndex} references out-of-range sample data (offset {off24}, length {len24}, pcm blob length {(pcmData?.Length ?? 0)}).");
                            }

                            var instData = new byte[len24];
                            if (len24 > 0)
                                Array.Copy(pcmData!, off24, instData, 0, len24);

                            instruments.Add(new PcmInstrument(
                                instIndex,
                                is16,
                                isStereo,
                                off24,
                                len24,
                                isLooped,
                                loopPt,
                                instData
                            ));
                        }

                        pcmContainer = new PcmContainer(lastIndex, instruments);
                    }
                    else
                    {
                        // Not a valid PCM header; treat as no PCM present.
                        // We already consumed those 4 bytes, but return whatever remains as pcmData if any.
                        using var remaining = new MemoryStream();
                        // include the 4 bytes already read
                        remaining.Write(phdr, 0, 4);
                        var buf = new byte[4096];
                        int r2;
                        while ((r2 = stream.Read(buf, 0, buf.Length)) > 0)
                        {
                            remaining.Write(buf, 0, r2);
                            absOffset += r2;
                        }
                        pcmData = remaining.Length == 0 ? null : remaining.ToArray();
                    }
                }
                else
                {
                    // pcmOffset == 0: no PCM header present. Nothing to do.
                }

                // Done parsing
                break;
            }
            else if (b >= 0x81 && b <= 0xFF)
            {
                // Delay command; value = lower 7 bits
                int ticks = b & 0x7F;
                // include the delay byte in the current block data
                current.WriteByte((byte)b);
                if (ticks >= _minPauseTicks)
                {
                    // finalize block including this delay byte
                    FinalizeCurrentBlock(blocks, current, blockStartOffset, endsWithPause: true, pauseTicks: ticks);
                    // prepare for next block
                    blockStartOffset = absOffset; // next byte will be start of next block
                }
                // else continue accumulating into same block
                continue;
            }
            else
            {
                // unknown opcode (should not happen by spec), treat it as single byte and include it
                current.WriteByte((byte)b);
                continue;
            }
        }

        // If there is leftover bytes after EOF marker in current MemoryStream they were finalized on EOF.
        return (header, blocks, pcmContainer, pcmData);
    }

    private static void FinalizeCurrentBlock(List<ZsmBlock> blocks, MemoryStream current, long startOffset, bool endsWithPause, int pauseTicks)
    {
        var arr = current.ToArray();
        int len = arr.Length;
        blocks.Add(new ZsmBlock(startOffset, len, arr, endsWithPause, pauseTicks));
        current.SetLength(0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadLe24(byte[] buf, int start)
    {
        return buf[start] | (buf[start + 1] << 8) | (buf[start + 2] << 16);
    }

    private static void ReadExactly(Stream s, byte[] buffer, int offset, int count, ref long absOffset)
    {
        int read;
        int pos = offset;
        int remaining = count;
        while (remaining > 0 && (read = s.Read(buffer, pos, remaining)) > 0)
        {
            pos += read;
            remaining -= read;
            absOffset += read;
        }
        if (remaining != 0) throw new EndOfStreamException("Unexpected end of stream while reading header/data");
    }

    private static void ReadAndWrite(Stream s, MemoryStream dst, int count, ref long absOffset)
    {
        const int BufSize = 4096;
        var buf = new byte[Math.Min(BufSize, Math.Max(1, count))];
        int remaining = count;
        while (remaining > 0)
        {
            int toRead = Math.Min(buf.Length, remaining);
            int r = s.Read(buf, 0, toRead);
            if (r == 0) throw new EndOfStreamException("Unexpected end of stream while reading command arguments");
            dst.Write(buf, 0, r);
            remaining -= r;
            absOffset += r;
        }
    }

    private static void ReadAndDiscard(Stream s, int count, ref long absOffset)
    {
        const int BufSize = 4096;
        var buf = new byte[Math.Min(BufSize, Math.Max(1, count))];
        int remaining = count;
        while (remaining > 0)
        {
            int toRead = Math.Min(buf.Length, remaining);
            int r = s.Read(buf, 0, toRead);
            if (r == 0) throw new EndOfStreamException("Unexpected end of stream while reading command arguments");
            remaining -= r;
            absOffset += r;
        }
    }

    private static int ReadByteOrThrow(Stream s, ref long absOffset)
    {
        int v = s.ReadByte();
        if (v < 0) throw new EndOfStreamException("Unexpected end of stream while parsing ZSM commands");
        absOffset++;
        return v;
    }
}
