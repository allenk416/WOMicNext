using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace WOMicNext;

// WO Mic framing reference: YuMic (MIT), see THIRD_PARTY_NOTICES.md.
internal static class Protocol
{
    public const int SampleRate = 48000;
    public const int AudioHeaderSize = 11;
    public static async Task SendAsync(Stream stream, byte command, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        byte[] packet = new byte[5 + payload.Length];
        packet[0] = command;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(packet.AsMemory(5));
        await stream.WriteAsync(packet, ct);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, byte command, CancellationToken ct)
    {
        byte[] header = new byte[5];
        await stream.ReadExactlyAsync(header, ct);
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(1));
        if (header[0] != command || size > 4096)
            throw new IOException("手机返回了不兼容的协议数据。请检查 WO Mic 版本。");
        byte[] payload = new byte[(int)size];
        await stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    public static async Task HandshakeAsync(Stream stream, int mediaPort, CancellationToken ct)
    {
        await SendAsync(stream, 0x65, new byte[] { 4, 4, 6, 2, 0, 0 }, ct);
        var hello = await ReadAsync(stream, 0x65, ct);
        if (hello.Length < 2 || hello[0] != 0 || hello[1] != 4)
            throw new IOException("手机端协议版本不受支持；此版本需要 WO Mic 协议 4。");
        byte[] media = { 2, 2, 0, 0, (byte)(mediaPort >> 8), (byte)mediaPort };
        await SendAsync(stream, 0x66, media, ct);
        CheckSuccess(await ReadAsync(stream, 0x66, ct));
        await SendAsync(stream, 0x67, ReadOnlyMemory<byte>.Empty, ct);
        CheckSuccess(await ReadAsync(stream, 0x67, ct));
    }

    private static void CheckSuccess(byte[] response)
    {
        if (response.Length < 1 || response[0] != 0)
            throw new IOException("手机拒绝连接。请确认 WO Mic 已启动，并关闭其他连接它的客户端。");
    }

    public static bool TryAudio(byte[] packet, out ushort sequence, out int length)
    {
        sequence = 0; length = 0;
        if (packet.Length <= AudioHeaderSize || packet[0] != 4) return false;
        int declared = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        length = declared - 7;
        if (length <= 0 || length > 1275 || length != packet.Length - AudioHeaderSize) return false;
        sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
        return true;
    }

    public static bool IsNewer(ushort current, ushort previous) => unchecked((short)(current - previous)) > 0;
}
