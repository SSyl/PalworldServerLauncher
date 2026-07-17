using System.Buffers.Binary;
using System.Text;

namespace PalServerLauncher.Rcon;

/// <summary>A decoded RCON packet: the echoed request id, the packet type, and the body text.</summary>
public readonly record struct RconMessage(int Id, int Type, string Body);

/// <summary>
/// Encode/decode for the Source RCON wire format (the protocol Palworld's RCON speaks). A packet is
/// <c>[Size int32][Id int32][Type int32][Body ...bytes 0x00][pad 0x00]</c>, all integers little-endian, where
/// Size counts every byte after the Size field itself (so <c>Size == 10 + bodyBytes</c>). Bodies are UTF-8:
/// commands are ASCII, but Palworld emits UTF-8 in responses (with its own known server-side truncation of
/// multi-byte player names). Pure and allocation-simple so it can be unit-tested without a socket.
/// </summary>
public static class RconPacket
{
    // Type constants. Note AuthResponse and ExecCommand deliberately share the value 2 (per the Source spec):
    // direction and context disambiguate them.
    public const int TypeResponseValue = 0;
    public const int TypeExecCommand = 2;
    public const int TypeAuthResponse = 2;
    public const int TypeAuth = 3;

    /// <summary>Size counts Id + Type + body + the body's null + one pad null, i.e. 10 bytes plus the body.</summary>
    public const int MinBodyFramePrefix = 10;

    /// <summary>Largest body we'll accept when decoding, a sanity bound so a bad Size can't make us allocate wildly.
    /// The Source protocol caps a single response body near 4 KB.</summary>
    public const int MaxBodyBytes = 4096;

    /// <summary>Build the full wire bytes (Size prefix included) for a packet with the given id, type, and body.</summary>
    public static byte[] Encode(int id, int type, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        var size = 4 + 4 + bodyBytes.Length + 2; // id + type + body + body-null + pad-null
        var buffer = new byte[4 + size];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), size);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), type);
        bodyBytes.CopyTo(buffer.AsSpan(12));
        // The two trailing null bytes are already zero-initialized.
        return buffer;
    }

    /// <summary>
    /// Decode a frame: the packet bytes AFTER the 4-byte Size field (i.e. Id + Type + body + nulls). The caller
    /// reads the Size, then that many bytes, then hands them here. The body runs from offset 8 to its null
    /// terminator (the trailing pad null is ignored).
    /// </summary>
    public static RconMessage Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8)
            throw new ArgumentException($"RCON frame too short ({frame.Length} bytes)", nameof(frame));

        var id = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(0, 4));
        var type = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(4, 4));

        var bodySpan = frame.Slice(8);
        var nul = bodySpan.IndexOf((byte)0);
        var bodyLength = nul >= 0 ? nul : bodySpan.Length;
        var body = Encoding.UTF8.GetString(bodySpan.Slice(0, bodyLength));
        return new RconMessage(id, type, body);
    }
}
