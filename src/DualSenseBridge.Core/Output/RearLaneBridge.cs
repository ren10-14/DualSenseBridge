using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Output;

/// <summary>
/// Мост нулевых «zero-fill» дорожек хаптик (v1.11.0 — фикс «жужжания» вибрации).
///
/// Протокол viiper V5: медиа-кадры 0x83 публикуются на часах 480 кадров
/// (10 мс, 100 Гц), а интервалы хаптик (rear-каналы) завершаются каждые
/// 512 кадров (10,667 мс, 93,75 Гц). «Each V5 media frame consumes one
/// completed rear interval OR ZERO-FILLS that lane» — каждый ~16-й кадр
/// приходит с ДОРОЖКОЙ ХАПТИК ИЗ НУЛЕЙ («нового интервала нет»), а не с
/// тишиной конца эффекта.
///
/// Проигрывание этих нулей актуаторами = периодические провалы сигнала
/// ⇒ вибрация превращается в жужжание/треск. Референс (hbashton/DS4Windows,
/// DualSenseRearHapticsUnderrunGate + PersistentBluetoothHapticsExpiryQpc)
/// мостирует их: шаблон хаптик УДЕРЖИВАЕТ последний реальный блок, конец
/// эффекта принадлежит только игре («publishing an explicit silent haptics
/// block»), а удержание ограничено ~3 блоками (32 мс — «Three blocks cover
/// that observed phase without treating persistent payload expiry as proof
/// that a source is still producing»).
///
/// Правила моста:
///  - реальный (ненулевой) блок → запоминается как последний реальный;
///  - нулевой блок И последний реальный свежее MaxHold → подставляется
///    последний реальный (мост);
///  - нулевой блок при несвежем/отсутствующем последнем → проходит как есть
///    (конец эффекта — игра владеет концом, не таймаут).
/// </summary>
public sealed class RearLaneBridge
{
    /// <summary>Максимум удержания: 3 блока × 10,667 мс ≈ 32 мс (+запас джиттера).</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMilliseconds(35);

    private byte[]? _lastReal;
    private DateTime _lastRealUtc = DateTime.MinValue;

    public long Bridged;   // zero-fill закрыт последним реальным блоком
    public long Released;  // нули ушли насквозь (конец эффекта)

    /// <summary>Дорожка хаптик [78..141] — нули (zero-fill или явная тишина).</summary>
    public static bool RearIsAllZeros(byte[] carrier)
    {
        for (int i = DualSenseFeedback.CarrierHapticsOffset;
             i < DualSenseFeedback.CarrierHapticsOffset + DualSenseFeedback.CarrierHapticsSize; i++)
            if (carrier[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// Мостит zero-fill прямо в носителе (модифицирует carrier). Вызывать ТОЛЬКО
    /// для блоков потока — «немой» блок (явная тишина/затухание) не мостится.
    /// </summary>
    public bool Bridge(byte[] carrier, DateTime nowUtc)
    {
        if (!RearIsAllZeros(carrier))
        {
            // Реальный интервал — запоминаем (владелец будущего моста).
            _lastReal ??= new byte[DualSenseFeedback.CarrierHapticsSize];
            Array.Copy(carrier, DualSenseFeedback.CarrierHapticsOffset, _lastReal, 0,
                DualSenseFeedback.CarrierHapticsSize);
            _lastRealUtc = nowUtc;
            return false;
        }

        if (_lastReal is not null && nowUtc - _lastRealUtc <= MaxHold)
        {
            // Zero-fill: нового интервала нет — удерживаем последний реальный
            // (сигнал актуаторов остаётся непрерывным, как на PS5).
            Array.Copy(_lastReal, 0, carrier, DualSenseFeedback.CarrierHapticsOffset,
                DualSenseFeedback.CarrierHapticsSize);
            Bridged++;
            return true;
        }

        // Нули держатся дольше MaxHold — это конец эффекта, а не zero-fill.
        Released++;
        return false;
    }

    public void Reset()
    {
        _lastReal = null;
        _lastRealUtc = DateTime.MinValue;
    }
}
