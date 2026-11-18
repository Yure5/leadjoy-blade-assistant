using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace LeadjoyBlade.Core.Protocol;

/// <summary>极简 MessagePack 编解码器，覆盖手柄协议所需的 map/array/int/string/bool/bin。</summary>
public static class MsgPack
{
    public static byte[] PackMap(params (string Key, object? Value)[] entries)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteMapHeader(buffer, entries.Length);
        foreach (var (key, value) in entries)
        {
            WriteString(buffer, key);
            WriteValue(buffer, value);
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] Pack(object? value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        WriteValue(buffer, value);
        return buffer.WrittenSpan.ToArray();
    }

    public static void WriteValue(IBufferWriter<byte> writer, object? value)
    {
        switch (value)
        {
            case null:
                WriteByte(writer, 0xC0);
                break;
            case bool b:
                WriteByte(writer, b ? (byte)0xC3 : (byte)0xC2);
                break;
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                WriteInteger(writer, Convert.ToInt64(value));
                break;
            case float f:
                WriteByte(writer, 0xCA);
                WriteRaw(writer, BitConverter.GetBytes(f));
                break;
            case double d:
                WriteByte(writer, 0xCB);
                WriteRaw(writer, BitConverter.GetBytes(d));
                break;
            case string s:
                WriteString(writer, s);
                break;
            case byte[] bin:
                WriteBin(writer, bin);
                break;
            case IEnumerable<KeyValuePair<string, object?>> map:
                var pairs = map.ToArray();
                WriteMapHeader(writer, pairs.Length);
                foreach (var kv in pairs)
                {
                    WriteString(writer, kv.Key);
                    WriteValue(writer, kv.Value);
                }
                break;
            case System.Collections.IDictionary dict:
                WriteMapHeader(writer, dict.Count);
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    WriteString(writer, entry.Key?.ToString() ?? string.Empty);
                    WriteValue(writer, entry.Value);
                }
                break;
            case System.Collections.IEnumerable list:
                var items = list.Cast<object?>().ToArray();
                WriteArrayHeader(writer, items.Length);
                foreach (var item in items) WriteValue(writer, item);
                break;
            default:
                WriteString(writer, value.ToString() ?? string.Empty);
                break;
        }
    }

    public static void WriteMapHeader(IBufferWriter<byte> writer, int count)
    {
        if (count <= 15) WriteByte(writer, (byte)(0x80 | count));
        else if (count <= ushort.MaxValue) { WriteByte(writer, 0xDE); WriteUInt16(writer, (ushort)count); }
        else { WriteByte(writer, 0xDF); WriteUInt32(writer, (uint)count); }
    }

    public static void WriteArrayHeader(IBufferWriter<byte> writer, int count)
    {
        if (count <= 15) WriteByte(writer, (byte)(0x90 | count));
        else if (count <= ushort.MaxValue) { WriteByte(writer, 0xDC); WriteUInt16(writer, (ushort)count); }
        else { WriteByte(writer, 0xDD); WriteUInt32(writer, (uint)count); }
    }

    public static void WriteString(IBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= 31) WriteByte(writer, (byte)(0xA0 | bytes.Length));
        else if (bytes.Length <= byte.MaxValue) { WriteByte(writer, 0xD9); WriteByte(writer, (byte)bytes.Length); }
        else if (bytes.Length <= ushort.MaxValue) { WriteByte(writer, 0xDA); WriteUInt16(writer, (ushort)bytes.Length); }
        else { WriteByte(writer, 0xDB); WriteUInt32(writer, (uint)bytes.Length); }
        WriteRaw(writer, bytes);
    }

    public static void WriteBin(IBufferWriter<byte> writer, byte[] value)
    {
        if (value.Length <= byte.MaxValue) { WriteByte(writer, 0xC4); WriteByte(writer, (byte)value.Length); }
        else if (value.Length <= ushort.MaxValue) { WriteByte(writer, 0xC5); WriteUInt16(writer, (ushort)value.Length); }
        else { WriteByte(writer, 0xC6); WriteUInt32(writer, (uint)value.Length); }
        WriteRaw(writer, value);
    }

    public static void WriteInteger(IBufferWriter<byte> writer, long value)
    {
        if (value >= 0)
        {
            if (value <= 0x7F) WriteByte(writer, (byte)value);
            else if (value <= byte.MaxValue) { WriteByte(writer, 0xCC); WriteByte(writer, (byte)value); }
            else if (value <= ushort.MaxValue) { WriteByte(writer, 0xCD); WriteUInt16(writer, (ushort)value); }
            else if (value <= uint.MaxValue) { WriteByte(writer, 0xCE); WriteUInt32(writer, (uint)value); }
            else { WriteByte(writer, 0xCF); WriteUInt64(writer, (ulong)value); }
        }
        else
        {
            if (value >= -32) WriteByte(writer, unchecked((byte)value));
            else if (value >= sbyte.MinValue) { WriteByte(writer, 0xD0); WriteByte(writer, unchecked((byte)(sbyte)value)); }
            else if (value >= short.MinValue) { WriteByte(writer, 0xD1); WriteUInt16(writer, unchecked((ushort)(short)value)); }
            else if (value >= int.MinValue) { WriteByte(writer, 0xD2); WriteUInt32(writer, unchecked((uint)(int)value)); }
            else { WriteByte(writer, 0xD3); WriteUInt64(writer, unchecked((ulong)value)); }
        }
    }

    public static void WriteByte(IBufferWriter<byte> writer, byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }

    public static void WriteRaw(IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        var span = writer.GetSpan(data.Length);
        data.CopyTo(span);
        writer.Advance(data.Length);
    }

    public static void WriteUInt16(IBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(2);
        BinaryPrimitives.WriteUInt16BigEndian(span, value);
        writer.Advance(2);
    }

    public static void WriteUInt32(IBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        writer.Advance(4);
    }

    public static void WriteUInt64(IBufferWriter<byte> writer, ulong value)
    {
        var span = writer.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        writer.Advance(8);
    }

    // ---------------- 解码 ----------------

    public static object? Unpack(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        return ReadValue(data, ref offset);
    }

    public static object? ReadValue(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length) throw new EndOfStreamException();
        var code = data[offset++];

        if (code <= 0x7F) return (long)code;
        if (code >= 0xE0) return (long)(sbyte)code;
        if (code >= 0x80 && code <= 0x8F) return ReadMap(data, ref offset, code & 0x0F);
        if (code >= 0x90 && code <= 0x9F) return ReadArray(data, ref offset, code & 0x0F);
        if (code >= 0xA0 && code <= 0xBF) return ReadString(data, ref offset, code & 0x1F);

        switch (code)
        {
            case 0xC0: return null;
            case 0xC2: return false;
            case 0xC3: return true;
            case 0xC4: return ReadBin(data, ref offset, data[offset++]);
            case 0xC5: { var n = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)); offset += 2; return ReadBin(data, ref offset, n); }
            case 0xC6: { var n = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)); offset += 4; return ReadBin(data, ref offset, (int)n); }
            case 0xCA: { var v = BinaryPrimitives.ReadSingleBigEndian(data.Slice(offset, 4)); offset += 4; return (double)v; }
            case 0xCB: { var v = BinaryPrimitives.ReadDoubleBigEndian(data.Slice(offset, 8)); offset += 8; return v; }
            case 0xCC: return (long)data[offset++];
            case 0xCD: { var v = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)); offset += 2; return (long)v; }
            case 0xCE: { var v = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)); offset += 4; return (long)v; }
            case 0xCF: { var v = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8)); offset += 8; return (long)v; }
            case 0xD0: return (long)(sbyte)data[offset++];
            case 0xD1: { var v = BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2)); offset += 2; return (long)v; }
            case 0xD2: { var v = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4)); offset += 4; return (long)v; }
            case 0xD3: { var v = BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8)); offset += 8; return v; }
            case 0xD9: return ReadString(data, ref offset, data[offset++]);
            case 0xDA: { var n = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)); offset += 2; return ReadString(data, ref offset, n); }
            case 0xDB: { var n = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)); offset += 4; return ReadString(data, ref offset, (int)n); }
            case 0xDC: { var n = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)); offset += 2; return ReadArray(data, ref offset, n); }
            case 0xDD: { var n = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)); offset += 4; return ReadArray(data, ref offset, (int)n); }
            case 0xDE: { var n = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2)); offset += 2; return ReadMap(data, ref offset, n); }
            case 0xDF: { var n = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)); offset += 4; return ReadMap(data, ref offset, (int)n); }
            default: throw new NotSupportedException($"MessagePack code 0x{code:X2} 未支持");
        }
    }

    private static Dictionary<string, object?> ReadMap(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var map = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = ReadValue(data, ref offset)?.ToString() ?? string.Empty;
            map[key] = ReadValue(data, ref offset);
        }
        return map;
    }

    private static List<object?> ReadArray(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var list = new List<object?>(count);
        for (var i = 0; i < count; i++) list.Add(ReadValue(data, ref offset));
        return list;
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        var s = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return s;
    }

    private static byte[] ReadBin(ReadOnlySpan<byte> data, ref int offset, int length)
    {
        var bin = data.Slice(offset, length).ToArray();
        offset += length;
        return bin;
    }
}
