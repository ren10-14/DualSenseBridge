namespace DualSenseBridge.Core.Protocol;

/// <summary>
/// Режимы (opcode) адаптивных триггеров DualSense.
/// Кодировка подтверждена:
///  - Nielk1 (John Klein), «Factories for all DualSense trigger effects» (MIT),
///    согласовано с официальными именами Sony (Apple GameController / Steamworks 1.55,
///    isteamdualsense.h: SCE_PAD_TRIGGER_EFFECT_MODE_*);
///  - фиксировано в moonlight-common-c / moonlight-qt (эффекты передаются как есть).
/// ВНИМАНИЕ: режимы 0xFC..0xFE — отладочные/калибровочные, они «ломают» состояние
/// триггера до нажатия кнопки сброса — жёстко блокируются (см. <see cref="IsSafeMode"/>).
/// </summary>
public enum TriggerEffectMode : byte
{
    None = 0x00,                 // нет эффекта (эквивалент отсутствия команды)
    SimpleFeedback = 0x01,       // остаточный «простой» режим, использовать не рекомендуется
    SimpleWeapon = 0x02,         // остаточный «простой» режим, использовать не рекомендуется
    Off = 0x05,                  // официально: выключить эффект, вернуть упор в нейтраль
    SimpleVibration = 0x06,      // остаточный «простой» режим, использовать не рекомендуется
    LimitedFeedback = 0x11,      // ограниченный диапазон, использовать не рекомендуется
    LimitedWeapon = 0x12,        // ограниченный диапазон, использовать не рекомендуется
    Feedback = 0x21,             // официально: сопротивление от позиции (0..9 / сила 0..8)
    Bow = 0x22,                  // неофициально: лук (сила + срыв)
    Galloping = 0x23,            // неофициально: галоп (две «ступни», частота)
    Weapon = 0x25,               // официально: «оружие» — стена в диапазоне позиций
    Vibration = 0x26,            // официально: вибрация триггера от позиции
    Machine = 0x27,              // неофициально: «машина» (две амплитуды, частота, период)
}

/// <summary>Блок эффекта триггера: 1 байт режима + 10 байт параметров.</summary>
public static class TriggerEffect
{
    public const int BlockSize = 11;

    /// <summary>Опасные (отладочные/калибровочные) режимы — запрещены к отправке.</summary>
    public static bool IsSafeMode(byte mode) => mode is not (0xFC or 0xFD or 0xFE);

    /// <summary>Режим из официального набора Sony (используется играми).</summary>
    public static bool IsOfficialMode(byte mode) =>
        mode is ((byte)TriggerEffectMode.Off) or ((byte)TriggerEffectMode.Feedback)
            or ((byte)TriggerEffectMode.Weapon) or ((byte)TriggerEffectMode.Vibration);

    /// <summary>
    /// Санитарная обработка блока: опасные режимы заменяются на Off.
    /// Возвращает true, если блок изменился.
    /// </summary>
    public static bool Sanitize(Span<byte> block)
    {
        if (block.Length != BlockSize)
            throw new ArgumentException($"Размер блока эффекта должен быть {BlockSize}", nameof(block));
        if (IsSafeMode(block[0]))
            return false;
        Off(block);
        return true;
    }

    /// <summary>Выключить эффект (официальный режим 0x05).</summary>
    public static void Off(Span<byte> block)
    {
        block.Clear();
        block[0] = (byte)TriggerEffectMode.Off;
    }

    public static byte[] Off()
    {
        var b = new byte[BlockSize];
        Off(b);
        return b;
    }

    /// <summary>
    /// Официальный эффект Feedback (0x21): сопротивление, начинающееся с позиции
    /// <paramref name="position"/> (0..9 — 10 зон хода триггера) с силой
    /// <paramref name="strength"/> (0..8; 0 = эффект выключается, как у Sony).
    /// Кодировка: [0]=0x21, [1..2]=битовая маска активных зон (LE16),
    /// [3..6]=упакованные 3-битные значения силы по зонам (LE), [7..10]=0.
    /// </summary>
    public static bool Feedback(Span<byte> block, byte position, byte strength)
    {
        if (position > 9) throw new ArgumentOutOfRangeException(nameof(position), "позиция 0..9");
        if (strength > 8) throw new ArgumentOutOfRangeException(nameof(strength), "сила 0..8");
        if (strength == 0) { Off(block); return true; }

        ZonesFrom(block, (byte)TriggerEffectMode.Feedback, position, 9, (byte)(strength - 1));
        return true;
    }

    /// <summary>
    /// Официальный эффект Weapon (0x25): «стена» между стартом и концом.
    /// start 2..7, end в (start..8], сила 0..8.
    /// Кодировка: [0]=0x25, [1..2]=маска битов start|end (LE16), [3]=сила-1.
    /// </summary>
    public static bool Weapon(Span<byte> block, byte startPosition, byte endPosition, byte strength)
    {
        if (startPosition < 2 || startPosition > 7)
            throw new ArgumentOutOfRangeException(nameof(startPosition), "старт 2..7");
        if (endPosition > 8 || endPosition <= startPosition)
            throw new ArgumentOutOfRangeException(nameof(endPosition), "конец в (start..8]");
        if (strength > 8) throw new ArgumentOutOfRangeException(nameof(strength), "сила 0..8");
        if (strength == 0) { Off(block); return true; }

        block.Clear();
        block[0] = (byte)TriggerEffectMode.Weapon;
        ushort zones = (ushort)((1 << startPosition) | (1 << endPosition));
        block[1] = (byte)(zones & 0xFF);
        block[2] = (byte)(zones >> 8);
        block[3] = (byte)((strength - 1) & 0x07);
        return true;
    }

    /// <summary>
    /// Официальный эффект Vibration (0x26): вибрация триггера, начиная с позиции.
    /// position 0..9, amplitude 0..8, frequency — частота в Гц (байт).
    /// Кодировка: как Feedback, но [9] = частота.
    /// </summary>
    public static bool Vibration(Span<byte> block, byte position, byte amplitude, byte frequency)
    {
        if (position > 9) throw new ArgumentOutOfRangeException(nameof(position), "позиция 0..9");
        if (amplitude > 8) throw new ArgumentOutOfRangeException(nameof(amplitude), "амплитуда 0..8");
        if (amplitude > 0 && frequency == 0) { Off(block); return true; }
        if (amplitude == 0) { Off(block); return true; }

        ZonesFrom(block, (byte)TriggerEffectMode.Vibration, position, 9, (byte)(amplitude - 1));
        block[9] = frequency;
        return true;
    }

    /// <summary>
    /// Сырой 10-зонный эффект MultiplePositionFeedback: индивидуальная сила (0..8)
    /// для каждой из 10 зон хода.
    /// </summary>
    public static bool MultiplePositionFeedback(Span<byte> block, ReadOnlySpan<byte> strengthByZone)
    {
        if (strengthByZone.Length != 10)
            throw new ArgumentException("нужны силы для 10 зон", nameof(strengthByZone));

        bool any = false;
        foreach (var s in strengthByZone) { if (s > 8) throw new ArgumentOutOfRangeException(nameof(strengthByZone), "сила 0..8"); if (s > 0) any = true; }
        if (!any) { Off(block); return true; }

        block.Clear();
        block[0] = (byte)TriggerEffectMode.Feedback;
        uint forceZones = 0;
        ushort activeZones = 0;
        for (int i = 0; i < 10; i++)
        {
            if (strengthByZone[i] == 0) continue;
            forceZones |= (uint)(((strengthByZone[i] - 1) & 0x07) << (3 * i));
            activeZones |= (ushort)(1 << i);
        }
        block[1] = (byte)(activeZones & 0xFF);
        block[2] = (byte)(activeZones >> 8);
        block[3] = (byte)(forceZones & 0xFF);
        block[4] = (byte)((forceZones >> 8) & 0xFF);
        block[5] = (byte)((forceZones >> 16) & 0xFF);
        block[6] = (byte)((forceZones >> 24) & 0xFF);
        return true;
    }

    /// <summary>Неофициальный эффект Bow (0x22): как Weapon + отдельная сила срыва.</summary>
    public static bool Bow(Span<byte> block, byte startPosition, byte endPosition, byte force, byte snapForce)
    {
        Weapon(block, startPosition, endPosition, force);
        block[0] = (byte)TriggerEffectMode.Bow;
        block[4] = (byte)((snapForce - 1) & 0x07);
        return true;
    }

    /// <summary>Произвольный блок — только безопасные режимы; иначе Off.</summary>
    public static byte[] Raw(byte mode, ReadOnlySpan<byte> parameters)
    {
        var b = new byte[BlockSize];
        if (!IsSafeMode(mode)) { Off(b); return b; }
        b[0] = mode;
        var n = Math.Min(10, parameters.Length);
        parameters[..n].CopyTo(b.AsSpan(1, n));
        return b;
    }

    // Заполнение зон [from..to] одинаковой 3-битной силой value (как в прошивке Sony).
    private static void ZonesFrom(Span<byte> block, byte mode, byte from, byte to, byte value)
    {
        block.Clear();
        block[0] = mode;
        uint zones = 0;
        ushort active = 0;
        for (int i = from; i <= to; i++)
        {
            zones |= (uint)(value << (3 * i));
            active |= (ushort)(1 << i);
        }
        block[1] = (byte)(active & 0xFF);
        block[2] = (byte)(active >> 8);
        block[3] = (byte)(zones & 0xFF);
        block[4] = (byte)((zones >> 8) & 0xFF);
        block[5] = (byte)((zones >> 16) & 0xFF);
        block[6] = (byte)((zones >> 24) & 0xFF);
    }
}
