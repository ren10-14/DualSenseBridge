using Concentus;
using Concentus.Enums;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Output;

/// <summary>
/// Кодировщик звука ДИНАМИКА DualSense (v1.12.0).
///
/// Протокол viiper V5: сервер НЕ кодирует звук динамика — носитель 0x36 уходит
/// с ПУСТОЙ Opus-дорожкой («Packet 0x13 … It is explicitly empty here»), а звук
/// едет отдельным хвостом кадра 0x83: ровно 1920 Б raw PCM (480 кадров
/// S16LE стерео 48 кГц = 10 мс). Кодировать в дорожку обязан МОСТ — так делает
/// DS4Windows («DS4Windows injects its already-encoded 200-byte Opus frame
/// before forwarding the report», docs/devices/dualsense.md: «the
/// physical-controller bridge supplies the encoded speaker lane»).
///
/// Проверенный на железе контракт (DS4Windows, DualSenseBluetoothSpeakerPassthrough,
/// «exact proven MeasuredTransport DualSense speaker encoder»):
///   - Opus 48 кГц, 2 канала, OPUS_APPLICATION_AUDIO;
///   - Битрейт 200×8×100 = 160 кбит/с, CBR (UseVBR=false), Complexity 0;
///   - кадр = 480 сэмплов (10 мс) → РОВНО 200 Б (короткий кадр НЕ дополнять
///     нулями — «changes its Opus packet structure and can make the controller
///     reject the combined report»);
///   - дорожка в 0x36: [142]=0x93, [143]=200, данные [144..343];
///   - при живом звуке глубина медиабуфера [5..9] = 0x80 («clean Windows
///     speaker traces keep every 0x36 media-lane depth at 0x80»);
///   - ПУСТАЯ дорожка (TLV с нулевой длиной) заставляет контроллер пищать
///     тревогу — «Empty speaker TLVs can make the controller emit an alert
///     tone»: когда кадра нет, [142] и [143] обязаны быть нулями.
/// </summary>
public sealed class SpeakerOpusEncoder
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSamples = 480;                 // 10 мс
    public const int PcmBytes = FrameSamples * Channels * 2; // 1920 Б S16LE
    public const int OpusBytes = 200;                    // CBR-кадр
    public const int LaneOffset = DualSenseFeedback.CarrierSpeakerOffset;      // 142
    public const int LaneDataOffset = DualSenseFeedback.CarrierSpeakerOffset + 2; // 144
    public const byte LanePacketType = 0x93;
    public const byte MediaBufferDepth = 0x80;           // [5..9] при живом звуке

    private readonly IOpusEncoder _encoder;
    private readonly short[] _pcm = new short[FrameSamples * Channels];

    public long Frames;
    public long Rejected;

    public SpeakerOpusEncoder()
    {
        _encoder = OpusCodecFactory.CreateEncoder(SampleRate, Channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = OpusBytes * 8 * 100; // 160 кбит/с CBR → ровно 200 Б на кадр
        _encoder.UseVBR = false;
        _encoder.Complexity = 0;
    }

    /// <summary>
    /// Кодирует 1920 Б PCM (S16LE стерео) и вписывает дорожку динамика прямо в
    /// носитель 0x36: [142]=0x93, [143]=200, [144..343]=Opus, [5..9]=0x80.
    /// false — кодировщик выдал не 200 Б (кадр отброшен, дорожка не тронута).
    /// </summary>
    public bool EncodeInto(byte[] carrier, ReadOnlySpan<byte> pcm)
    {
        if (carrier is null || carrier.Length != DualSenseProtocol.CombinedBtReportSize ||
            pcm.Length != PcmBytes)
            return false;

        for (int i = 0; i < _pcm.Length; i++)
            _pcm[i] = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));

        int encoded;
        lock (_encoder)
        {
            encoded = _encoder.Encode(_pcm.AsSpan(), FrameSamples,
                carrier.AsSpan(LaneDataOffset, OpusBytes), OpusBytes);
        }
        if (encoded != OpusBytes)
        {
            Rejected++;
            return false;
        }

        carrier[LaneOffset] = LanePacketType;
        carrier[LaneOffset + 1] = OpusBytes;
        // Глубина медиабуфера для чистого звука (контракт референса).
        carrier[5] = MediaBufferDepth;
        carrier[6] = MediaBufferDepth;
        carrier[7] = MediaBufferDepth;
        carrier[8] = MediaBufferDepth;
        carrier[9] = MediaBufferDepth;
        Frames++;
        return true;
    }
}
