using DualSenseBridge.Core.Viiper;
using Xunit;

namespace DualSenseBridge.Tests;

/// <summary>Тесты кадрового кодека V5 (VIIPER, диалект hbashton).</summary>
public class V5FrameTests
{
    [Fact]
    public void Encode_MatchesGoldenVector()
    {
        // Независимо вычислено (python+zlib): VPCM|0x05|0x81|len=3|seq=7|payload DEADBE.
        byte[] frame = V5Frame.Encode(V5Frame.TypeFeedback, sequence: 7, payload: new byte[] { 0xDE, 0xAD, 0xBE });
        Assert.Equal("5650434d0581030007000000cf395a5f" + "deadbe", Convert.ToHexString(frame).ToLowerInvariant());
    }

    [Fact]
    public void EncodeDecode_RoundTrip()
    {
        var payload = new byte[V5Frame.InputStateSize];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);

        byte[] frame = V5Frame.Encode(V5Frame.TypeInputState, 12345, payload);
        Assert.True(V5Frame.TryDecode(frame, out byte type, out uint seq, out var decoded, out bool broken, out int consumed));
        Assert.False(broken);
        Assert.Equal(V5Frame.TypeInputState, type);
        Assert.Equal(12345u, seq);
        Assert.Equal(payload, decoded);
        Assert.Equal(frame.Length, consumed);
    }

    [Fact]
    public void TryDecode_FullFeedbackFrame_Parses()
    {
        // Полный кадр обратной связи 0x81: 474 байта payload.
        var payload = new byte[V5Frame.FeedbackPayloadSize];
        payload[0] = 0x80; // rumbleSmall
        byte[] frame = V5Frame.Encode(V5Frame.TypeFeedback, 42, payload);

        Assert.True(V5Frame.TryDecode(frame, out byte type, out _, out var decoded, out bool broken, out _));
        Assert.False(broken);
        Assert.Equal(V5Frame.TypeFeedback, type);
        Assert.Equal(474, decoded.Length);
        Assert.Equal(0x80, decoded[0]);
    }

    [Fact]
    public void TryDecode_BrokenCrc_SignalsBroken()
    {
        byte[] frame = V5Frame.Encode(V5Frame.TypeFeedback, 1, new byte[4]);
        frame[14] ^= 0xFF; // портим CRC
        Assert.False(V5Frame.TryDecode(frame, out _, out _, out _, out bool broken, out _));
        Assert.True(broken); // по контракту V5 такое соединение нужно закрыть
    }

    [Fact]
    public void TryDecode_BadMagic_SignalsBroken()
    {
        byte[] frame = V5Frame.Encode(V5Frame.TypeFeedback, 1, new byte[4]);
        frame[0] = (byte)'X';
        Assert.False(V5Frame.TryDecode(frame, out _, out _, out _, out bool broken, out _));
        Assert.True(broken);
    }

    [Fact]
    public void TryDecode_PartialFrame_WaitsWithoutBroken()
    {
        byte[] frame = V5Frame.Encode(V5Frame.TypeFeedback, 1, new byte[64]);
        // Отрезаем половину — данных мало, но протокол не нарушен.
        Assert.False(V5Frame.TryDecode(frame[..40], out _, out _, out _, out bool broken, out _));
        Assert.False(broken);
    }

    [Fact]
    public void TryDecode_TwoFramesInBuffer_SequentialDecode()
    {
        byte[] f1 = V5Frame.Encode(V5Frame.TypeInputState, 1, new byte[10]);
        byte[] f2 = V5Frame.Encode(V5Frame.TypeFeedbackAudio, 2, new byte[20]);
        var buffer = f1.Concat(f2).ToArray();

        Assert.True(V5Frame.TryDecode(buffer, out byte t1, out _, out _, out _, out int c1));
        Assert.Equal(V5Frame.TypeInputState, t1);
        Assert.Equal(f1.Length, c1);

        Assert.True(V5Frame.TryDecode(buffer[c1..], out byte t2, out _, out _, out _, out int c2));
        Assert.Equal(V5Frame.TypeFeedbackAudio, t2);
        Assert.Equal(f2.Length, c2);
    }
}
