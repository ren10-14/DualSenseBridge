namespace DualSenseBridge.App.UI;

/// <summary>
/// Темы интерфейса (v1.13.0): светлая/тёмная. WinForms не имеет встроенной
/// тёмной темы — применяем палитру рекурсивно ко всем контроллам окна.
/// Panel не перекрашиваем (например, квадрат-превью цвета подсветки), TrackBar
/// остаётся системным. Выбор — настройка Theme (light/dark), применяется на лету.
/// </summary>
internal static class UiTheme
{
    public static readonly Color DarkBack = Color.FromArgb(0x1E, 0x1E, 0x1E);
    public static readonly Color DarkSurface = Color.FromArgb(0x25, 0x25, 0x25);
    public static readonly Color DarkControl = Color.FromArgb(0x2D, 0x2D, 0x2D);
    public static readonly Color DarkBorder = Color.FromArgb(0x4A, 0x4A, 0x4A);
    public static readonly Color DarkText = Color.FromArgb(0xE6, 0xE6, 0xE6);
    public static readonly Color DarkLogBack = Color.FromArgb(0x14, 0x14, 0x14);
    public static readonly Color DarkLogText = Color.FromArgb(0xD4, 0xD4, 0xD4);

    public static readonly Color LightBack = Color.FromArgb(0xF0, 0xF0, 0xF0);
    public static readonly Color LightSurface = SystemColors.Control;
    public static readonly Color LightControl = SystemColors.Control;
    public static readonly Color LightText = SystemColors.ControlText;
    public static readonly Color LightLogBack = Color.White;
    public static readonly Color LightLogText = SystemColors.ControlText;

    public static Color ForeColor(bool dark) => dark ? DarkText : LightText;

    /// <summary>Применяет тему ко всем контроллам (кроме Panel — превью цвета!).</summary>
    public static void Apply(Control root, bool dark)
    {
        root.BackColor = dark ? DarkBack : LightBack;
        root.ForeColor = dark ? DarkText : LightText;
        ApplyChildren(root, dark);
    }

    private static void ApplyChildren(Control parent, bool dark)
    {
        foreach (Control c in parent.Controls)
        {
            switch (c)
            {
                case Button b:
                    if (dark)
                    {
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = DarkControl;
                        b.ForeColor = DarkText;
                        b.FlatAppearance.BorderColor = DarkBorder;
                    }
                    else
                    {
                        b.FlatStyle = FlatStyle.Standard;
                        b.BackColor = LightControl;
                        b.ForeColor = LightText;
                    }
                    break;
                case TextBox tb:
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    tb.BackColor = dark ? DarkLogBack : LightLogBack;
                    tb.ForeColor = dark ? DarkLogText : LightLogText;
                    break;
                case ComboBox cmb:
                    cmb.BackColor = dark ? DarkControl : LightControl;
                    cmb.ForeColor = dark ? DarkText : LightText;
                    break;
                case NumericUpDown num:
                    num.BackColor = dark ? DarkControl : Color.White;
                    num.ForeColor = dark ? DarkText : LightText;
                    break;
                case GroupBox gb:
                    // GroupBox рисует рамку и заголовок сам — цвет фона обязателен,
                    // иначе дети с прозрачным фоном рисуются поверх чужого цвета.
                    gb.BackColor = dark ? DarkSurface : LightBack;
                    gb.ForeColor = dark ? DarkText : LightText;
                    break;
                case CheckBox chk:
                    chk.BackColor = Color.Transparent; // фон родителя
                    chk.ForeColor = dark ? DarkText : LightText;
                    break;
                case Label lbl:
                    lbl.BackColor = Color.Transparent;
                    lbl.ForeColor = dark ? DarkText : LightText;
                    break;
                case Panel pnl:
                    // «swatch» — превью цвета подсветки, не перекрашиваем;
                    // остальные панели (напр., строка статистики) темируются.
                    if ((pnl.Tag as string) != "swatch")
                        pnl.BackColor = dark ? DarkSurface : LightBack;
                    break;
                default:
                    c.BackColor = dark ? DarkSurface : LightBack;
                    c.ForeColor = dark ? DarkText : LightText;
                    break;
            }
            ApplyChildren(c, dark);
        }
    }
}
