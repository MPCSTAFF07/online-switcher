namespace Ocs.Core;

public readonly record struct RtpPayload(byte[] Data, uint Timestamp, ushort Sequence, int Marker);
public static class Rtp
{
    public static RtpPayload? Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12 || packet[0] >> 6 != 2) return null;
        int start = 12 + (packet[0] & 15) * 4;
        if (packet.Length < start) return null;
        if ((packet[0] & 16) != 0)
        {
            if (packet.Length < start + 4) return null;
            start += 4 + ((packet[start + 2] << 8) | packet[start + 3]) * 4;
        }
        int end = packet.Length;
        if ((packet[0] & 32) != 0)
        {
            int padding = packet[^1];
            if (padding == 0 || padding > end - start) return null;
            end -= padding;
        }
        if (start >= end) return null;
        uint ts = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(packet[4..8]);
        ushort seq = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        return new(packet[start..end].ToArray(), ts, seq, packet[1] >> 7);
    }
}
