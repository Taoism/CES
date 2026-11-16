using System.Buffers.Binary;
using System.Text;

namespace NothingElseCompares2u.Disc;

/// <summary>Big-endian cursor over a byte array (Blu-ray and DVD structures are big-endian).</summary>
internal sealed class BeReader(byte[] data, int pos = 0)
{
    public byte[] Data { get; } = data;
    public int Pos { get; set; } = pos;
    public int Length => Data.Length;

    public void Need(int n)
    {
        if (Pos < 0 || Pos + n > Data.Length)
            throw new InvalidDataException($"Unexpected end of structure at {Pos} (+{n}, size {Data.Length}).");
    }

    public byte U8()
    {
        Need(1);
        return Data[Pos++];
    }

    public ushort U16()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(Data.AsSpan(Pos));
        Pos += 2;
        return v;
    }

    public uint U32()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(Data.AsSpan(Pos));
        Pos += 4;
        return v;
    }

    public string Ascii(int n)
    {
        Need(n);
        var s = Encoding.ASCII.GetString(Data, Pos, n);
        Pos += n;
        return s;
    }

    public void Skip(int n)
    {
        Need(n);
        Pos += n;
    }

    public static ushort U16At(byte[] d, int at) => at + 2 <= d.Length ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(at)) : (ushort)0;

    public static uint U32At(byte[] d, int at) => at + 4 <= d.Length ? BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(at)) : 0;
}
