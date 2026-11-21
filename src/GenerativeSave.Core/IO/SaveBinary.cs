using System.Buffers.Binary;
using System.Text;

namespace GenerativeSave.Core.IO;

/// <summary>
/// Little-endian reader over a byte span, mirroring UE's FArchive for the
/// subset of types this save format uses.
/// </summary>
public sealed class SaveReader
{
    private readonly byte[] _data;

    public SaveReader(byte[] data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    public byte[] Buffer => _data;
    public int Position { get; set; }
    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;
    public bool AtEnd => Position >= _data.Length;

    public void Seek(int pos)
    {
        if (pos < 0 || pos > _data.Length)
            throw new SaveFormatException($"seek out of range: {pos} (len {_data.Length})");
        Position = pos;
    }

    public ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0) throw new SaveFormatException($"negative read {n} @{Position}");
        if (Position + n > _data.Length)
            throw new SaveFormatException(
                $"need {n} bytes @{Position}, only {Remaining} left");
        var s = new ReadOnlySpan<byte>(_data, Position, n);
        Position += n;
        return s;
    }

    public void Skip(int n) => Take(n);

    public byte U8() => Take(1)[0];
    public sbyte I8() => unchecked((sbyte)Take(1)[0]);
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public short I16() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    /// <summary>UE FString: int32 length (negative = UTF-16), incl. NUL.</summary>
    public string FString()
    {
        int n = I32();
        if (n == 0) return string.Empty;
        if (n > 0)
        {
            var bytes = Take(n);
            // trailing NUL is included in n
            int len = n;
            while (len > 0 && bytes[len - 1] == 0) len--;
            return Encoding.UTF8.GetString(bytes[..len]);
        }
        else
        {
            int chars = -n;
            var bytes = Take(chars * 2);
            int len = chars;
            while (len > 0 && bytes[len * 2 - 1] == 0 && bytes[len * 2 - 2] == 0) len--;
            return Encoding.Unicode.GetString(bytes[..(len * 2)]);
        }
    }

    public string FName() => FString();

    public Guid Guid() => new(Take(16));

    public byte[] Raw(int n) => Take(n).ToArray();

    public uint PeekU32()
    {
        int save = Position;
        try { return U32(); }
        finally { Position = save; }
    }

    public string PeekFString()
    {
        int save = Position;
        try { return FString(); }
        catch { return string.Empty; }
        finally { Position = save; }
    }
}

/// <summary>Little-endian writer producing the same byte layout.</summary>
public sealed class SaveWriter
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _bw;

    public SaveWriter() => _bw = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);

    public long Position => _ms.Position;
    public byte[] ToArray() => _ms.ToArray();

    public void U8(byte v) => _bw.Write(v);
    public void I8(sbyte v) => _bw.Write(v);
    public void U16(ushort v) => _bw.Write(v);
    public void I16(short v) => _bw.Write(v);
    public void U32(uint v) => _bw.Write(v);
    public void I32(int v) => _bw.Write(v);
    public void U64(ulong v) => _bw.Write(v);
    public void I64(long v) => _bw.Write(v);
    public void F32(float v) => _bw.Write(v);
    public void F64(double v) => _bw.Write(v);
    public void Bytes(ReadOnlySpan<byte> b) => _bw.Write(b);

    public void FString(string? s)
    {
        s ??= string.Empty;
        if (s.Length == 0)
        {
            I32(0);
            return;
        }
        bool ascii = true;
        foreach (char c in s)
            if (c > 0x7F) { ascii = false; break; }

        if (ascii)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            I32(bytes.Length + 1);
            _bw.Write(bytes);
            _bw.Write((byte)0);
        }
        else
        {
            var bytes = Encoding.Unicode.GetBytes(s);
            I32(-(s.Length + 1));
            _bw.Write(bytes);
            _bw.Write((ushort)0);
        }
    }

    public void FName(string? s) => FString(s);
    public void Guid(Guid g) => _bw.Write(g.ToByteArray());
}

public sealed class SaveFormatException : Exception
{
    public SaveFormatException(string message) : base(message) { }
}
