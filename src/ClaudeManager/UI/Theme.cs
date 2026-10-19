using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeManager.UI;

/// <summary>
/// Windows 11-style look that follows the system: light/dark app mode and the accent color.
/// Colors only; fonts and glyphs are in <see cref="Fonts"/>.
/// </summary>
internal sealed class Theme
{
    public bool Dark { get; private init; }
    public Color Accent { get; private init; }
    public Color AccentText { get; private init; }      // text on accent fill
    public Color AccentHover { get; private init; }
    public Color Background { get; private init; }       // flyout
    public Color Card { get; private init; }
    public Color CardHover { get; private init; }
    public Color CardBorder { get; private init; }
    public Color Subtle { get; private init; }           // secondary button fill
    public Color SubtleHover { get; private init; }
    public Color SubtlePressed { get; private init; }
    public Color Text { get; private init; }
    public Color TextSecondary { get; private init; }
    public Color TextTertiary { get; private init; }
    public Color Divider { get; private init; }
    public Color Success { get; private init; }
    public Color Warning { get; private init; }
    public Color WarningBack { get; private init; }
    public Color Danger { get; private init; }
    public Color WindowBorder { get; private init; }

    public static Theme Current { get; private set; } = Detect();

    /// <summary>Re-reads the system settings. Returns true if anything changed.</summary>
    public static bool Refresh()
    {
        var t = Detect();
        bool changed = t.Dark != Current.Dark || t.Accent != Current.Accent;
        Current = t;
        return changed;
    }

    static Theme Detect()
    {
        bool dark = false;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = k?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { }
        // Developer override for screenshots/testing: CLAUDE_MANAGER_THEME=dark|light.
        var forced = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_THEME");
        if (string.Equals(forced, "dark", StringComparison.OrdinalIgnoreCase)) dark = true;
        else if (string.Equals(forced, "light", StringComparison.OrdinalIgnoreCase)) dark = false;
        if (SystemInformation.HighContrast) return HighContrast();

        var accent = SystemAccent() ?? Color.FromArgb(0, 103, 192);
        // Accent shades as Windows uses them: lighter in dark mode, darker in light mode, for contrast.
        var a = dark ? Mix(accent, Color.White, 0.35) : Mix(accent, Color.Black, 0.1);
        return dark
            ? new Theme
            {
                Dark = true, Accent = a, AccentHover = Mix(a, Color.White, 0.12), AccentText = Luma(a) > 0.55 ? Color.Black : Color.White,
                Background = Color.FromArgb(32, 32, 32), Card = Color.FromArgb(43, 43, 43), CardHover = Color.FromArgb(50, 50, 50),
                CardBorder = Color.FromArgb(29, 29, 29), Subtle = Color.FromArgb(55, 55, 55), SubtleHover = Color.FromArgb(62, 62, 62),
                SubtlePressed = Color.FromArgb(48, 48, 48), Text = Color.FromArgb(255, 255, 255), TextSecondary = Color.FromArgb(200, 200, 200),
                TextTertiary = Color.FromArgb(150, 150, 150), Divider = Color.FromArgb(56, 56, 56), Success = Color.FromArgb(108, 203, 95),
                Warning = Color.FromArgb(252, 225, 0), WarningBack = Color.FromArgb(67, 53, 25), Danger = Color.FromArgb(255, 153, 164),
                WindowBorder = Color.FromArgb(70, 70, 70),
            }
            : new Theme
            {
                Dark = false, Accent = a, AccentHover = Mix(a, Color.White, 0.12), AccentText = Luma(a) > 0.6 ? Color.Black : Color.White,
                Background = Color.FromArgb(243, 243, 243), Card = Color.FromArgb(251, 251, 251), CardHover = Color.FromArgb(246, 246, 246),
                CardBorder = Color.FromArgb(229, 229, 229), Subtle = Color.FromArgb(255, 255, 255), SubtleHover = Color.FromArgb(246, 246, 246),
                SubtlePressed = Color.FromArgb(240, 240, 240), Text = Color.FromArgb(26, 26, 26), TextSecondary = Color.FromArgb(96, 96, 96),
                TextTertiary = Color.FromArgb(138, 138, 138), Divider = Color.FromArgb(229, 229, 229), Success = Color.FromArgb(15, 123, 15),
                Warning = Color.FromArgb(157, 93, 0), WarningBack = Color.FromArgb(255, 244, 206), Danger = Color.FromArgb(196, 43, 28),
                WindowBorder = Color.FromArgb(210, 210, 210),
            };
    }

    static Theme HighContrast() => new()
    {
        Dark = SystemColors.Window.GetBrightness() < 0.5f, Accent = SystemColors.Highlight, AccentHover = SystemColors.Highlight, AccentText = SystemColors.HighlightText,
        Background = SystemColors.Window, Card = SystemColors.Window, CardHover = SystemColors.Window, CardBorder = SystemColors.WindowText,
        Subtle = SystemColors.ButtonFace, SubtleHover = SystemColors.Highlight, SubtlePressed = SystemColors.Highlight,
        Text = SystemColors.WindowText, TextSecondary = SystemColors.WindowText, TextTertiary = SystemColors.GrayText, Divider = SystemColors.WindowText,
        Success = SystemColors.WindowText, Warning = SystemColors.WindowText, WarningBack = SystemColors.Window, Danger = SystemColors.WindowText,
        WindowBorder = SystemColors.WindowText,
    };

    [DllImport("dwmapi.dll")] static extern int DwmGetColorizationColor(out uint color, out bool opaque);

    static Color? SystemAccent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int abgr)
                return Color.FromArgb(abgr & 0xFF, (abgr >> 8) & 0xFF, (abgr >> 16) & 0xFF);
            if (DwmGetColorizationColor(out var argb, out _) == 0)
                return Color.FromArgb((int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF));
        }
        catch { }
        return null;
    }

    public static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    static double Luma(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    // ---------- window chrome (Windows 11 DWM; harmless no-ops elsewhere) ----------

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Rounded corners + themed 1px border (Windows 11), dark title/scrollbars where applicable.</summary>
    public static void ApplyChrome(Form f, Theme t)
    {
        if (!f.IsHandleCreated) return;
        int dark = t.Dark ? 1 : 0;
        DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        int border = ColorTranslator.ToWin32(t.WindowBorder);
        DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }

    // ---------- drawing helpers ----------

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void FillRounded(Graphics g, Color c, RectangleF r, float radius)
    {
        using var p = Rounded(r, radius);
        using var b = new SolidBrush(c);
        g.FillPath(b, p);
    }

    public static void StrokeRounded(Graphics g, Color c, RectangleF r, float radius, float width = 1f)
    {
        using var p = Rounded(r, radius);
        using var pen = new Pen(c, width);
        g.DrawPath(pen, p);
    }
}

/// <summary>Segoe UI Variable (Windows 11) or Segoe UI, and the Fluent/MDL2 icon font. Created once.</summary>
internal static class Fonts
{
    static readonly string TextFamily = Installed("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    static readonly string DisplayFamily = Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI Semibold";
    static readonly string IconFamily = Installed("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    public static readonly Font Body = new(TextFamily, 9f);
    public static readonly Font BodyStrong = new(TextFamily, 9f, FontStyle.Bold);
    public static readonly Font Caption = new(TextFamily, 8f);
    public static readonly Font Title = new(DisplayFamily, 11.5f, FontStyle.Bold);
    public static readonly Font Name = new(DisplayFamily, 10.5f, FontStyle.Bold);
    public static readonly Font Icon = new(IconFamily, 10f);
    public static readonly Font IconSmall = new(IconFamily, 8f);

    static bool Installed(string family)
    {
        try { using var f = new FontFamily(family); return true; } catch { return false; }
    }
}

/// <summary>Segoe Fluent Icons / MDL2 code points.</summary>
internal static class Glyph
{
    public const string Settings = "\uE713", Refresh = "\uE72C", Pin = "\uE718", Pinned = "\uE840", Close = "\uE8BB",
        Add = "\uE710", More = "\uE712", Desktop = "\uE7F4", Terminal = "\uE756", Warning = "\uE7BA", Check = "\uE73E",
        Error = "\uE783", Info = "\uE946", Chevron = "\uE70D", Folder = "\uE8B7";
}

/// <summary>Themed context menus (rounded selection, Windows 11 spacing, dark mode).</summary>
internal sealed class FluentMenuRenderer : ToolStripProfessionalRenderer
{
    readonly Theme t;
    public FluentMenuRenderer(Theme theme) : base(new Colors(theme)) { t = theme; RoundedEdges = true; }

    public static void Apply(ToolStrip strip)
    {
        var th = Theme.Current;
        strip.Renderer = new FluentMenuRenderer(th);
        strip.Font = Fonts.Body;
        strip.BackColor = th.Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        strip.ForeColor = th.Text;
        strip.Padding = new Padding(2, 4, 2, 4);
        if (strip is ToolStripDropDown dd) dd.DropShadowEnabled = true;
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        Theme.FillRounded(e.Graphics, t.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(235, 235, 235), r, 4);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? t.Text : t.TextTertiary;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = t.TextSecondary;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        TextRenderer.DrawText(g, Glyph.Check, Fonts.IconSmall, r, t.Accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using var pen = new Pen(t.Divider);
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(t.Dark ? Color.FromArgb(64, 64, 64) : Color.FromArgb(220, 220, 220));
        e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
    }

    sealed class Colors : ProfessionalColorTable
    {
        readonly Theme t;
        public Colors(Theme theme) { t = theme; UseSystemColors = false; }
        Color Back => t.Dark ? Color.FromArgb(44, 44, 44) : Color.FromArgb(249, 249, 249);
        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => t.Divider;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => t.Dark ? Color.FromArgb(60, 60, 60) : Color.FromArgb(235, 235, 235);
        public override Color SeparatorDark => t.Divider;
        public override Color SeparatorLight => Color.Transparent;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Color.Transparent;
        public override Color CheckPressedBackground => Color.Transparent;
    }
}
