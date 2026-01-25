using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ZsmCompress;
public sealed record ZsmHeader(
    byte Version,
    int LoopPoint,       // 24-bit little-endian
    int PcmOffset,       // 24-bit little-endian (0 = no PCM)
    byte FmChannelMask,
    ushort PsgChannelMask,
    ushort TickRate
);

[DebuggerDisplay("Length={Length} Hash={DataHashHex,nq}")]
public sealed record ZsmBlock(
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
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }
}

/// <summary>
/// Parses a ZSM file and splits the music stream into blocks that end in a "pause".
/// A "pause" here is a Delay command (0x81-0xFF) whose ticks >= minPauseTicks, or EOF (0x80).
/// The parser consumes the ZSM header and the music stream up to the 0x80 marker. PCM header/data after 0x80 are not interpreted
/// by the block splitting but are left unread by ParseStream (caller can continue reading if desired).
/// 
/// New: The parser can optionally exclude EXTCMD blocks (0x40 and following bytes) from the returned block data
/// by setting includeExtCmds = false. The parser will still consume those bytes from the stream (advancing offsets),
/// but they will not be written into the block byte arrays.
/// </summary>
public sealed class ZsmParser
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

    public (ZsmHeader Header, List<ZsmBlock> Blocks) ParseFile(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ParseStream(fs);
    }

    public (ZsmHeader Header, List<ZsmBlock> Blocks) ParseStream(Stream stream)
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
                // Advance pointer to PCM header if caller wants to continue reading; parsing of PCM optional
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
        return (header, blocks);
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