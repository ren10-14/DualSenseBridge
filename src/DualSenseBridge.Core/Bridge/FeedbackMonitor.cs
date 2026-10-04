using System.Text;
using DualSenseBridge.Core.Protocol;
using DualSenseBridge.Core.Viiper;

namespace DualSenseBridge.Core.Bridge;

/// <summary>
/// Диагностика обратной связи от игры (v1.5.0). Строит компактную сводку каждого
/// принятого кадра отдачи и сообщает об ИЗМЕНЕНИЯХ: в журнал попадает каждое
/// новое состояние эффектов (флаги, моторы, режимы триггеров, RGB, player LED),
/// а не спам одинаковых кадров. Это позволяет по журналу точно определить,
/// ЧТО игра реально отправляет виртуальному DualSense.
/// </summary>
public sealed class FeedbackMonitor
{
    private string _lastSummary = string.Empty;
    private DateTime _lastInfoUtc = DateTime.MinValue;
    private long _frames;
    private long _changes;
    private DateTime _lastFrameUtc;
    private DateTime _lastChangeUtc;

    /// <summary>Сводка последнего кадра (для UI).</summary>
    public string LastSummary { get; private set; } = "пока ничего не прислано";

    public long Frames => _frames;
    public long Changes => _changes;
    public DateTime LastFrameUtc => _lastFrameUtc;

    /// <summary>
    /// Регистрирует кадр (счётчики — всегда, построение строки — не чаще 2 раз/с:
    /// при активном аудиоканале кадры идут ~200 Гц, и сборка сводки на каждом
    /// кадре перегревала CPU). Возвращает сводку для журнала, если она изменилась.
    /// </summary>
    public string? Register(DualSenseFeedback fb)
    {
        _frames++;
        _lastFrameUtc = DateTime.UtcNow;

        bool infoDue = _frames == 1 || (_lastFrameUtc - _lastInfoUtc).TotalMilliseconds >= 500;
        if (!infoDue) return null;
        _lastInfoUtc = _lastFrameUtc;

        string summary = Describe(fb);
        LastSummary = summary;
        if (summary != _lastSummary)
        {
            _lastSummary = summary;
            _changes++;
            _lastChangeUtc = _lastFrameUtc;
            return summary;
        }
        return null;
    }

    /// <summary>Человекочитаемая сводка кадра отдачи (все поля, «всё-всё»).</summary>
    public static string Describe(DualSenseFeedback fb)
    {
        var sb = new StringBuilder(160);

        // Нативный отчёт игры 0x02 (общий блок 47 Б).
        var u = fb.UsbOutputReport;
        if (u.Length == DualSenseProtocol.OutputReportUsbSize && u[0] == DualSenseProtocol.OutputReportUsb)
        {
            byte f0 = u[1], f1 = u[2];
            byte f2 = u[1 + DualSenseProtocol.OffValidFlag2];
            sb.Append($"флаги {f0:X2}/{f1:X2}/{f2:X2}");

            byte mR = u[1 + DualSenseProtocol.OffMotorRight];
            byte mL = u[1 + DualSenseProtocol.OffMotorLeft];
            sb.Append($", моторы {mR}/{mL}");

            byte r2 = u[1 + DualSenseProtocol.OffTriggerRight];
            byte l2 = u[1 + DualSenseProtocol.OffTriggerLeft];
            sb.Append($", R2={(r2 == 0 ? "нет" : $"0x{r2:X2}")}");
            sb.Append($", L2={(l2 == 0 ? "нет" : $"0x{l2:X2}")}");

            byte r = u[1 + DualSenseProtocol.OffLightbarRed];
            byte g = u[1 + DualSenseProtocol.OffLightbarGreen];
            byte b = u[1 + DualSenseProtocol.OffLightbarBlue];
            sb.Append($", RGB #{r:X2}{g:X2}{b:X2}");

            byte pl = u[1 + DualSenseProtocol.OffPlayerLeds];
            sb.Append($", playerLED 0x{pl:X2}");
        }
        else
        {
            sb.Append("нативного отчёта нет (только структурированные поля)");
        }

        // Структурированные поля viiper [0..27].
        sb.Append($" | viiper: моторы {fb.RumbleSmall}/{fb.RumbleLarge}");
        sb.Append($", R2={(fb.TriggerRight[0] == 0 ? "нет" : $"0x{fb.TriggerRight[0]:X2}")}");
        sb.Append($", L2={(fb.TriggerLeft[0] == 0 ? "нет" : $"0x{fb.TriggerLeft[0]:X2}")}");
        sb.Append($", RGB #{fb.LedRed:X2}{fb.LedGreen:X2}{fb.LedBlue:X2}");
        sb.Append($", playerLED 0x{fb.PlayerLeds:X2}");

        // Аудиоканал (комбинированный BT-носитель 0x36).
        sb.Append(fb.HasCombinedCarrier
            ? " | АУДИОКАНАЛ 0x36 (хаптики PCM + динамик)"
            : " | аудиоканал 0x36: пуст");

        return sb.ToString();
    }
}
