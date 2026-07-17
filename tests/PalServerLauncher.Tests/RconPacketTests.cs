using System;
using System.Buffers.Binary;
using System.Text;
using PalServerLauncher.Rcon;

namespace PalServerLauncher.Tests;

public class RconPacketTests
{
    [Fact]
    public void Encode_lays_out_size_id_type_body_and_two_null_terminators()
    {
        // Encode(id=1, type=3 (AUTH), "pass"): size = 4+4+4(body)+2 = 14, total wire = 18 bytes.
        var wire = RconPacket.Encode(1, RconPacket.TypeAuth, "pass");

        Assert.Equal(18, wire.Length);
        Assert.Equal(14, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(0)));   // Size = remainder
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(4)));    // Id
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(8)));    // Type
        Assert.Equal("pass", Encoding.ASCII.GetString(wire, 12, 4));                // Body
        Assert.Equal(0, wire[16]);                                                  // body null terminator
        Assert.Equal(0, wire[17]);                                                  // pad null
    }

    [Fact]
    public void Encode_size_is_ten_plus_body_length()
    {
        var wire = RconPacket.Encode(7, RconPacket.TypeExecCommand, "ShowPlayers");
        var size = BinaryPrimitives.ReadInt32LittleEndian(wire.AsSpan(0));
        Assert.Equal(10 + Encoding.UTF8.GetByteCount("ShowPlayers"), size);
    }

    [Theory]
    [InlineData(1, RconPacket.TypeAuth, "secret")]
    [InlineData(42, RconPacket.TypeExecCommand, "Broadcast hello")]
    [InlineData(0, RconPacket.TypeResponseValue, "")]                  // empty body round-trips
    [InlineData(-1, RconPacket.TypeAuthResponse, "")]                  // auth-failure sentinel id
    [InlineData(999, RconPacket.TypeResponseValue, "multi\nline\ttext")]
    public void Encode_then_Decode_round_trips(int id, int type, string body)
    {
        var wire = RconPacket.Encode(id, type, body);
        // Decode consumes the frame after the 4-byte Size prefix.
        var message = RconPacket.Decode(wire.AsSpan(4));

        Assert.Equal(id, message.Id);
        Assert.Equal(type, message.Type);
        Assert.Equal(body, message.Body);
    }

    [Fact]
    public void Decode_reads_utf8_bodies()
    {
        var wire = RconPacket.Encode(5, RconPacket.TypeResponseValue, "Ｐ両"); // multi-byte
        var message = RconPacket.Decode(wire.AsSpan(4));
        Assert.Equal("Ｐ両", message.Body);
    }

    [Fact]
    public void Decode_stops_at_the_body_null_and_ignores_trailing_bytes()
    {
        // Frame = id(4) + type(4) + "hi" + null + garbage. The body must end at the first null.
        var frame = new byte[4 + 4 + 2 + 1 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0), 5);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), RconPacket.TypeResponseValue);
        frame[8] = (byte)'h';
        frame[9] = (byte)'i';
        frame[10] = 0;                    // body terminator
        frame[11] = (byte)'X';            // trailing bytes past the terminator must be ignored
        frame[12] = (byte)'Y';
        frame[13] = (byte)'Z';

        var message = RconPacket.Decode(frame);
        Assert.Equal("hi", message.Body);
    }

    [Fact]
    public void Decode_throws_on_a_frame_too_short_for_the_header() =>
        Assert.Throws<ArgumentException>(() => RconPacket.Decode(new byte[4]));
}
