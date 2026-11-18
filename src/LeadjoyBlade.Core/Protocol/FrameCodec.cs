using System.Buffers;

namespace LeadjoyBlade.Core.Protocol;

/// <summary>
/// 风刃/Leadjoy 自定义串口帧格式：
///   短帧: A5 LEN CC PAYLOAD... CHECKSUM
///   长帧: A4 LEN CC SEQ  PAYLOAD... CHECKSUM   (SEQ 低 7 位为剩余分片数，首片最高位置 1)
/// 手柄状态上报使用同一外层帧，TYPE 为 0x20。
/// 来源：Android MsgPackPacket / MapperManager.extractFramesLocked。
/// </summary>
public sealed class FrameReader
{
    public const byte HeaderShort = 0xA5;
    public const byte HeaderLong = 0xA4;
    public const byte TypeMsgPack = 0xCC;
    public const byte TypeGamepad = 0x20;

    private readonly List<byte> _buffer = new(1024);
    private readonly Dictionary<int, byte[]> _fragments = new();
    private int _expectedFragments;

    public IReadOnlyList<Frame> Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);
        var frames = new List<Frame>();

        var i = 0;
        while (i < _buffer.Count)
        {
            var header = _buffer[i];
            if (header is not (HeaderShort or HeaderLong))
            {
                i++;
                continue;
            }

            if (_buffer.Count - i < 4) break;

            var length = _buffer[i + 1];
            if (length < 4)
            {
                i++;
                continue;
            }

            if (length > _buffer.Count - i) break;

            var raw = _buffer.GetRange(i, length).ToArray();
            if (!VerifyChecksum(raw))
            {
                i++;
                continue;
            }

            var type = raw[2];
            Frame? frame = null;
            if (header == HeaderShort)
            {
                var payload = raw.AsSpan(3, length - 4).ToArray();
                frame = new Frame(type, payload, 0, false);
            }
            else
            {
                var seq = raw[3];
                var payload = raw.AsSpan(4, length - 5).ToArray();
                frame = Reassemble(seq, payload, type);
            }

            if (frame != null) frames.Add(frame);
            i += length;
        }

        if (i > 0) _buffer.RemoveRange(0, i);
        if (_buffer.Count > 4096) _buffer.Clear();
        return frames;
    }

    private Frame? Reassemble(byte seq, byte[] payload, byte type)
    {
        var index = seq & 0x7F;
        if (index == 0) return null;
        if ((seq & 0x80) != 0)
        {
            _fragments.Clear();
            _expectedFragments = index;
        }
        _fragments[index] = payload;
        if (_fragments.Count != _expectedFragments) return null;

        var total = 0;
        for (var n = _expectedFragments; n >= 1; n--)
            if (_fragments.TryGetValue(n, out var part)) total += part.Length;

        var result = new byte[total];
        var offset = 0;
        for (var n = _expectedFragments; n >= 1; n--)
        {
            if (!_fragments.TryGetValue(n, out var part)) return null;
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        _fragments.Clear();
        _expectedFragments = 0;
        return new Frame(type, result, seq, true);
    }

    public static bool VerifyChecksum(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 4) return false;
        var sum = 0;
        for (var i = 0; i < raw.Length - 1; i++) sum += raw[i];
        return (byte)sum == raw[^1];
    }

    public static byte Checksum(ReadOnlySpan<byte> data, int start, int length)
    {
        var sum = 0;
        for (var i = 0; i < length; i++) sum += data[start + i];
        return (byte)sum;
    }

    /// <summary>把一段 MessagePack 载荷封装为短帧/长帧。</summary>
    public static List<byte[]> Encode(ReadOnlySpan<byte> payload, int mtu)
    {
        var frames = new List<byte[]>();
        if (payload.Length == 0) return frames;

        var maxShort = Math.Min(mtu - 4, 251);
        if (payload.Length <= maxShort)
        {
            var frame = new byte[payload.Length + 4];
            frame[0] = HeaderShort;
            frame[1] = (byte)frame.Length;
            frame[2] = TypeMsgPack;
            payload.CopyTo(frame.AsSpan(3));
            frame[^1] = Checksum(frame, 0, frame.Length - 1);
            frames.Add(frame);
            return frames;
        }

        var chunk = Math.Min(mtu - 5, 200);
        var count = (int)Math.Ceiling(payload.Length / (double)chunk);
        if (count > 127) throw new InvalidOperationException($"载荷过大：{payload.Length} 字节");

        var sourceOffset = 0;
        for (var i = 0; i < count; i++)
        {
            var size = Math.Min(chunk, payload.Length - sourceOffset);
            var frame = new byte[size + 5];
            frame[0] = HeaderLong;
            frame[1] = (byte)frame.Length;
            frame[2] = TypeMsgPack;
            var seq = (count - i) & 0x7F;
            if (i == 0) seq |= 0x80;
            frame[3] = (byte)seq;
            payload.Slice(sourceOffset, size).CopyTo(frame.AsSpan(4));
            frame[^1] = Checksum(frame, 0, frame.Length - 1);
            frames.Add(frame);
            sourceOffset += size;
        }
        return frames;
    }
}

public sealed record Frame(byte Type, byte[] Payload, int Sequence, bool IsLong)
{
    public bool IsGamepad => Type is FrameReader.TypeGamepad or 0xE0;
    public string ToHex() => Convert.ToHexString(Payload);
}
