using System.IO.Compression;
using GenerativeSave.Core.IO;

namespace GenerativeSave.Core.Container;

/// <summary>
/// The .sav container used by every module file.
///
/// Layout (repeated per chunk):
///   8B  magic  C1 83 2A 9E 22 22 22 22
///   4B  u32    block size (131072)
///   4B  u32    flags (0)
///   1B  u8     method (3 = zlib)
///   8B  u64    compressed size
///   8B  u64    decompressed size
///   8B  u64    compressed size   (repeated)
///   8B  u64    decompressed size (repeated)
///   ..  zlib stream
///
/// The concatenated decompressed chunks are:
///   4B  u32  (gvas length + 4)
///   4B  u32  gvas length
///   ..  the GVAS payload
/// </summary>
public static class SaveContainer
{
    public static readonly byte[] ChunkMagic = { 0xC1, 0x83, 0x2A, 0x9E, 0x22, 0x22, 0x22, 0x22 };
    public const int ChunkHeaderSize = 49;      // magic + 41 bytes
    public const int BlockSize = 128 * 1024;    // 131072
    public const byte MethodZlib = 3;

    private static readonly byte[] GvasMagic = "GVAS"u8.ToArray();

    public static bool IsChunked(ReadOnlySpan<byte> b) =>
        b.Length >= ChunkHeaderSize && b[..8].SequenceEqual(ChunkMagic);

    public static bool IsPlainGvas(ReadOnlySpan<byte> b) =>
        b.Length >= 4 && b[..4].SequenceEqual(GvasMagic);

    /// <summary>Decompress the chunked container, returning the GVAS payload.</summary>
    public static byte[] Unwrap(byte[] file)
    {
        if (IsPlainGvas(file))
            return file;
        if (!IsChunked(file))
            throw new SaveFormatException("not a save container (bad magic)");

        var raw = new MemoryStream();
        int pos = 0;
        while (pos < file.Length)
        {
            if (pos + ChunkHeaderSize > file.Length)
                throw new SaveFormatException($"truncated chunk header @{pos}");
            if (!file.AsSpan(pos, 8).SequenceEqual(ChunkMagic))
                throw new SaveFormatException($"bad chunk magic @{pos}");

            int blockSize = BitConverter.ToInt32(file, pos + 8);
            byte method = file[pos + 16];
            long compSize = BitConverter.ToInt64(file, pos + 17);

            int dataStart = pos + ChunkHeaderSize;
            if (compSize < 0 || dataStart + compSize > file.Length)
                throw new SaveFormatException($"chunk @{pos} declares {compSize} compressed bytes");

            if (method != MethodZlib)
                throw new SaveFormatException($"unsupported chunk method {method} @{pos}");

            var comp = file.AsSpan(dataStart, (int)compSize);
            using var input = new MemoryStream(comp.ToArray());
            using var z = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw.Write(output.ToArray());

            pos = dataStart + (int)compSize;
        }

        var payload = raw.ToArray();
        if (payload.Length < 8)
            throw new SaveFormatException("container payload too short");

        // 8-byte length prefix, then GVAS
        int declared = BitConverter.ToInt32(payload, 4);
        int gvasStart = 8;
        if (payload.Length - gvasStart != declared)
        {
            // fall back to locating the magic rather than trusting the prefix
            int j = IndexOf(payload, GvasMagic, 0);
            if (j < 0) throw new SaveFormatException("no GVAS payload");
            gvasStart = j;
        }

        return payload[gvasStart..];
    }

    /// <summary>Re-encode a GVAS payload into the chunked container.</summary>
    public static byte[] Wrap(byte[] gvas, int blockSize = BlockSize, byte method = MethodZlib)
    {
        // rebuild the 8-byte prefix
        var payload = new byte[8 + gvas.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), gvas.Length + 4);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), gvas.Length);
        gvas.CopyTo(payload, 8);

        var outStream = new MemoryStream();
        for (int off = 0; off < payload.Length; off += blockSize)
        {
            int n = Math.Min(blockSize, payload.Length - off);
            var comp = Deflate(payload, off, n);

            var header = new byte[ChunkHeaderSize];
            ChunkMagic.CopyTo(header, 0);
            BitConverter.TryWriteBytes(header.AsSpan(8, 4), blockSize);
            BitConverter.TryWriteBytes(header.AsSpan(12, 4), 0);
            header[16] = method;
            BitConverter.TryWriteBytes(header.AsSpan(17, 8), (long)comp.Length);
            BitConverter.TryWriteBytes(header.AsSpan(25, 8), (long)n);
            BitConverter.TryWriteBytes(header.AsSpan(33, 8), (long)comp.Length);
            BitConverter.TryWriteBytes(header.AsSpan(41, 8), (long)n);

            outStream.Write(header);
            outStream.Write(comp);
        }
        return outStream.ToArray();
    }

    private static byte[] Deflate(byte[] data, int offset, int count)
    {
        using var outStream = new MemoryStream();
        using (var z = new ZLibStream(outStream, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data, offset, count);
        return outStream.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        for (int i = start; i <= haystack.Length - needle.Length; i++)
        {
            bool ok = true;
            for (int k = 0; k < needle.Length; k++)
                if (haystack[i + k] != needle[k]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
