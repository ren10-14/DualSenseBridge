using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Output;

/// <summary>
/// Джиттер-буфер аудиоканала хаптик (v1.7.0).
///
/// Почему нужен: viiper публикует блоки хаптик на часах 93,75 Гц (64 Б = 21,3 мс
/// звука на 3 кГц), причём ОДИН И ТОТ ЖЕ блок может прийти ДВАЖДЫ (кадр 0x83 и
/// его «realtime»-двойник 0x84). Писать в Bluetooth «как пришло» — рвано:
/// подряд два одинаковых блока, потом пауза — актуаторы играют «тук-тук»
/// (классический buffer underrun, как щелчки в аудио). Лечение стандартное
/// (jitter buffer + leaky bucket, ср. GStreamer leaky=downstream):
///   - ограниченная очередь, при переполнении выбрасывается СТАРЕЙШИЙ блок;
///   - дубликаты (одинаковая дорожка хаптик + состояние) отбрасываются;
///   - потребитель читает с РОВНЫМ темпом (один блок ~10,5 мс);
///   - при прекращении потока достаточно ОДНОГО «немого» блока (хаптики в ноль,
///     CRC пересчитан) — контроллер плавно замолкает, канал не заливается.
/// </summary>
public sealed class HapticsChannel
{
    private readonly object _lock = new();
    private readonly Queue<byte[]> _queue = new();
    private readonly int _capacity;
    private byte[]? _lastAccepted;
    private byte _lastAcceptedType;
    private byte[]? _lastWritten;

    public long Enqueued;
    public long DroppedDuplicates;
    public long DroppedOverflow;
    public long DroppedBackoff;
    public long Written;
    public long MuteFlushes;

    /// <summary>Время последнего принятого блока (UTC) — для детекции «поток кончился».</summary>
    public DateTime LastEnqueueUtc { get; private set; }
    private DateTime _lastAcceptUtc = DateTime.MinValue;

    /// <summary>
    /// Окно дедупликации: работает ТОЛЬКО для пары «0x83, затем его realtime-
    /// двойник 0x84» (viiper может прислать один блок дважды). Прочие одинаковые
    /// подряд блоки (ровный тон, тишина — нормальный поток на 93,75–100 Гц)
    /// НЕ дедуплицируются никогда: ложное отбрасывание легитимных блоков
    /// давало «пропадания звука» (v1.9.9).
    /// </summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMilliseconds(8);

    public HapticsChannel(int capacity = 16) => _capacity = Math.Max(2, capacity);

    public int Count { get { lock (_lock) return _queue.Count; } }

    /// <summary>
    /// Ставит блок (398 Б, ownership передаётся каналу). Дубликат отбрасывается
    /// ТОЛЬКО в одном случае: блок пришёл кадром 0x84 (realtime) сразу после
    /// такого же блока в кадре 0x83 (viiper шлёт один блок дважды). Переполнение
    /// — выбрасывается старейший блок. Возвращает true, если блок принят.
    /// </summary>
    public bool Enqueue(byte[] carrier, byte frameType = V5Frame.TypeFeedbackAudio)
    {
        if (carrier is null || carrier.Length != DualSenseProtocol.CombinedBtReportSize || carrier[0] != 0x36)
            return false;

        lock (_lock)
        {
            var now = DateTime.UtcNow;

            // Дубликат = кадр 0x84 сразу после такого же 0x83 (пара-двойник).
            // Всё остальное (одинаковые блоки ровного тона/тишины) — легитимный
            // поток, отбрасывать нельзя (v1.9.9: ложные отбрасывания = пропадания звука).
            if (frameType == V5Frame.TypeRealtimeHaptics &&
                _lastAcceptedType == V5Frame.TypeFeedbackAudio &&
                _lastAccepted is not null && ContentEquals(_lastAccepted, carrier) &&
                now - _lastAcceptUtc < DuplicateWindow)
            {
                DroppedDuplicates++;
                return false;
            }

            if (_queue.Count >= _capacity)
            {
                _queue.Dequeue(); // leaky bucket: жертвуем старейшим
                DroppedOverflow++;
            }

            _queue.Enqueue(carrier);
            _lastAccepted = carrier;
            _lastAcceptedType = frameType;
            _lastAcceptUtc = now;
            Enqueued++;
            LastEnqueueUtc = now;
            return true;
        }
    }

    /// <summary>
    /// Осознанный сброс старейшего блока (деградация канала): канал не тянет
    /// полный поток 0x36 — лучше потерять блок, чем завалить контроллер.
    /// </summary>
    public void DropOldest()
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                _queue.Dequeue();
                DroppedBackoff++;
            }
        }
    }

    /// <summary>Забирает очередной блок (null — очередь пуста).</summary>
    public byte[]? TryDequeue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return null;
            var block = _queue.Dequeue();
            _lastWritten = block;
            Written++;
            return block;
        }
    }

    /// <summary>
    /// «Немой» блок для плавного завершения потока: последний записанный блок,
    /// дорожка хаптик [78..141] занулена, CRC пересчитан. null — если ничего
    /// не писали или уже замолчали.
    /// </summary>
    public byte[]? BuildMuteBlock()
    {
        lock (_lock)
        {
            if (_lastWritten is null) return null;
            var mute = (byte[])_lastWritten.Clone();
            for (int i = DualSenseFeedback.CarrierHapticsOffset;
                 i < DualSenseFeedback.CarrierHapticsOffset + DualSenseFeedback.CarrierHapticsSize; i++)
                mute[i] = 0;
            // v1.12.0: кадр динамика НЕ повторяем («an old Opus frame cannot be
            // replayed» — референс): убираем TLV целиком, иначе последний кадр
            // звука проиграется дважды.
            mute[DualSenseFeedback.CarrierSpeakerOffset] = 0;
            mute[DualSenseFeedback.CarrierSpeakerOffset + 1] = 0;
            WriteCrc(mute);
            _lastWritten = mute;
            MuteFlushes++;
            return mute;
        }
    }

    public void Clear()
    {
        lock (_lock) _queue.Clear();
    }

    /// <summary>Содержимое совпадает (дорожка хаптик + заголовок динамика + состояние)?</summary>
    private static bool ContentEquals(byte[] a, byte[] b)
    {
        for (int i = DualSenseFeedback.CarrierHapticsOffset;
             i < DualSenseFeedback.CarrierHapticsOffset + DualSenseFeedback.CarrierHapticsSize; i++)
            if (a[i] != b[i]) return false;
        if (a[DualSenseFeedback.CarrierSpeakerOffset] != b[DualSenseFeedback.CarrierSpeakerOffset] ||
            a[DualSenseFeedback.CarrierSpeakerOffset + 1] != b[DualSenseFeedback.CarrierSpeakerOffset + 1])
            return false;
        for (int i = 0; i < DualSenseProtocol.OutputCommonSize; i++)
            if (a[DualSenseFeedback.CarrierStateOffset + i] != b[DualSenseFeedback.CarrierStateOffset + i]) return false;
        return true;
    }

    private static void WriteCrc(byte[] carrier)
    {
        uint crc = Crc32.Update(DualSenseFeedback.CarrierCrcSeed,
            carrier.AsSpan(0, DualSenseFeedback.CarrierCrcOffset));
        carrier[DualSenseFeedback.CarrierCrcOffset + 0] = (byte)(crc & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 1] = (byte)((crc >> 8) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 2] = (byte)((crc >> 16) & 0xFF);
        carrier[DualSenseFeedback.CarrierCrcOffset + 3] = (byte)((crc >> 24) & 0xFF);
    }
}
