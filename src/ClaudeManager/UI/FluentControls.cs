using System.ComponentModel;
using System.Drawing.Drawing2D;
using ClaudeManager.Core;

namespace ClaudeManager.UI;

internal enum ButtonKind { Standard, Accent, Subtle, Dashed }

/// <summary>
/// Owner-drawn Windows 11-style button (keeps Button's keyboard, focus and accessibility behavior).
/// <see cref="Surface"/> is the color behind it, so the rounded corners blend in.
/// </summary>
internal sealed class FluentButton : Button
{
    // Built in code only (no designer), so these are never serialized.
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ButtonKind Kind { get; set; } = ButtonKind.Standard;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public string? GlyphText { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool DropDown { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Surface { get; set; } = Theme.Current.Background;
    bool hover, pressed;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public FluentButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        Cursor = Cursors.Hand;
        Font = Fonts.Body;
        Margin = Padding.Empty;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { pressed = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    /// <summary>Width that fits glyph + text + chevron.</summary>
    public int PreferredWidth()
    {
        int w = S(24);
        if (GlyphText != null) w += S(16) + (string.IsNullOrEmpty(Text) ? 0 : S(8));
        if (!string.IsNullOrEmpty(Text)) w += TextRenderer.MeasureText(Text, Font).Width;
        if (DropDown) w += S(18);
        return w;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        float radius = S(5);

        Color fill = Surface, fore = Enabled ? t.Text : t.TextTertiary;
        switch (Kind)
        {
            case ButtonKind.Accent:
                fill = !Enabled ? t.Subtle : pressed ? Theme.Mix(t.Accent, t.Background, 0.2) : hover ? t.AccentHover : t.Accent;
                if (Enabled) fore = t.AccentText;
                Theme.FillRounded(g, fill, r, radius);
                break;
            case ButtonKind.Subtle:
                if (hover || pressed) { fill = pressed ? t.SubtlePressed : t.SubtleHover; Theme.FillRounded(g, fill, r, radius); }
                break;
            case ButtonKind.Dashed:
                if (hover || pressed) { fill = pressed ? t.SubtlePressed : t.CardHover; Theme.FillRounded(g, fill, r, radius); }
                using (var path = Theme.Rounded(r, S(7)))
                using (var pen = new Pen(hover ? t.Accent : t.TextTertiary, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawPath(pen, path);
                if (hover && Enabled) fore = t.Accent;
                break;
            default:
                fill = pressed ? t.SubtlePressed : hover ? t.SubtleHover : t.Subtle;
                Theme.FillRounded(g, fill, r, radius);
                Theme.StrokeRounded(g, t.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222), r, radius);
                break;
        }

        // Content: [glyph] [text] [chevron], centered.
        int glyphW = GlyphText != null ? S(16) : 0;
        int gap = GlyphText != null && !string.IsNullOrEmpty(Text) ? S(8) : 0;
        int textW = string.IsNullOrEmpty(Text) ? 0 : TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        int chevW = DropDown ? S(16) : 0;
        int total = glyphW + gap + textW + chevW;
        int x = Math.Max(S(8), (Width - total) / 2);
        const TextFormatFlags center = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        if (GlyphText != null)
        {
            TextRenderer.DrawText(g, GlyphText, Fonts.Icon, new Rectangle(x, 0, glyphW, Height), fore, fill, center | TextFormatFlags.HorizontalCenter);
            x += glyphW + gap;
        }
        if (textW > 0)
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, Math.Min(textW + 2, Width - x - chevW - S(4)), Height), fore, fill,
                center | TextFormatFlags.EndEllipsis);
            x += textW;
        }
        if (DropDown)
            TextRenderer.DrawText(g, Glyph.Chevron, Fonts.IconSmall, new Rectangle(x + S(4), 0, S(12), Height), Kind == ButtonKind.Accent ? fore : t.TextSecondary, fill,
                center | TextFormatFlags.HorizontalCenter);

        if (Focused && ShowFocusCues)
            Theme.StrokeRounded(g, t.Text, new RectangleF(1.5f, 1.5f, Width - 3.5f, Height - 3.5f), radius, 1.5f);
    }
}

/// <summary>One profile: icon, name, live status, and Desktop / Terminal actions.</summary>
internal sealed class ProfileCard : Control
{
    public Profile Profile { get; }
    public FluentButton Desktop { get; } = new() { Text = "Desktop", GlyphText = Glyph.Desktop, Kind = ButtonKind.Accent };
    public FluentButton Terminal { get; } = new() { Text = "Terminal", GlyphText = Glyph.Terminal, DropDown = true };
    public FluentButton More { get; } = new() { GlyphText = Glyph.More, Kind = ButtonKind.Subtle };

    ProfileUsage? usage;
    bool hover;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    public static int CardHeight(int dpi) => (int)Math.Round(104 * dpi / 96.0);

    public ProfileCard(Profile p)
    {
        Profile = p;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Controls.AddRange(new Control[] { Desktop, Terminal, More });
        foreach (Control c in Controls)
        {
            c.MouseEnter += (_, _) => SetHover(true);
            c.MouseLeave += (_, _) => SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position)));
        }
        Desktop.AccessibleName = $"Open {p.DesktopTitle}";
        Terminal.AccessibleName = $"Open {p.TerminalTitle}";
        More.AccessibleName = $"More actions for {p.Name}";
        AccessibleName = p.Name;
        AccessibleRole = AccessibleRole.Grouping;
        ApplySurface();
    }

    public void SetUsage(ProfileUsage? u)
    {
        if (Equals(u, usage)) return;
        usage = u;
        Invalidate();
    }

    void SetHover(bool on)
    {
        if (hover == on) return;
        hover = on;
        ApplySurface();
        Invalidate();
    }

    void ApplySurface()
    {
        var surface = hover ? Theme.Current.CardHover : Theme.Current.Card;
        foreach (FluentButton b in new[] { Desktop, Terminal, More }) { b.Surface = surface; b.Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { SetHover(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { SetHover(ClientRectangle.Contains(PointToClient(Cursor.Position))); base.OnMouseLeave(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = S(12), bh = S(32);
        More.Bounds = new Rectangle(Width - pad - S(32) + S(4), pad - S(2), S(32), S(32));
        int by = Height - pad - bh;
        int bw = (Width - 2 * pad - S(8)) / 2;
        Desktop.Bounds = new Rectangle(pad, by, bw, bh);
        Terminal.Bounds = new Rectangle(pad + bw + S(8), by, Width - 2 * pad - bw - S(8), bh);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var card = hover ? t.CardHover : t.Card;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, card, r, S(8));
        Theme.StrokeRounded(g, t.CardBorder, r, S(8));

        int pad = S(12), icon = S(36);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(IconFactory.ProfileImage(Profile, icon), pad, pad);

        int tx = pad + icon + S(12);
        int tw = More.Left - tx - S(4);
        TextRenderer.DrawText(g, Profile.Name, Fonts.Name, new Rectangle(tx, pad - S(1), tw, S(20)), t.Text, card,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        DrawStatus(g, t, card, new Rectangle(tx, pad + S(21), tw, S(18)));
    }

    void DrawStatus(Graphics g, Theme t, Color card, Rectangle r)
    {
        const TextFormatFlags flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
        if (usage != null)
        {
            // Live: green dot + what's running and its memory.
            int d = S(7);
            using (var b = new SolidBrush(t.Success)) g.FillEllipse(b, r.X, r.Y + (r.Height - d) / 2f, d, d);
            var parts = new List<string>();
            if (usage.DesktopRunning) parts.Add($"Desktop {UsageMonitor.Format(usage.DesktopBytes)}");
            if (usage.Terminals > 0) parts.Add($"{(usage.Terminals == 1 ? "Terminal" : $"{usage.Terminals} terminals")} {UsageMonitor.Format(usage.TerminalBytes)}");
            TextRenderer.DrawText(g, "Running · " + string.Join(" · ", parts), Fonts.Caption, new Rectangle(r.X + d + S(6), r.Y, r.Width - d - S(6), r.Height),
                t.Success, card, flags);
            return;
        }
        if (!Profile.IsDefault && !Profile.DesktopSignedIn)
        {
            // Pill: needs its first sign-in.
            const string label = "First sign-in needed";
            var sz = TextRenderer.MeasureText(g, label, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding);
            var pill = new RectangleF(r.X, r.Y + S(1), sz.Width + S(14), r.Height - S(2));
            Theme.FillRounded(g, t.WarningBack, pill, pill.Height / 2);
            TextRenderer.DrawText(g, label, Fonts.Caption, Rectangle.Round(pill), t.Warning, t.WarningBack,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return;
        }
        var text = Profile.IsDefault ? "Your existing setup · unchanged" : Paths.Shorten(Profile.CliConfigDir ?? "");
        TextRenderer.DrawText(g, text, Fonts.Caption, r, t.TextSecondary, card, flags | TextFormatFlags.PathEllipsis);
    }
}

/// <summary>Small status line: colored glyph, text, optional action link.</summary>
internal sealed class StatusChip : Control
{
    public enum Level { Checking, Ok, Warn, Bad }
    Level level;
    string text = "", action = "";
    public Action? OnAction { get; private set; }
    bool hover;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public StatusChip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.StaticText;
    }

    public void Set(Level l, string label, string? actionText = null, Action? onAction = null)
    {
        level = l; text = label; action = actionText ?? ""; OnAction = onAction;
        Cursor = onAction != null ? Cursors.Hand : Cursors.Default;
        AccessibleName = label + (actionText != null ? $". {actionText}" : "");
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { OnAction?.Invoke(); base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        var back = Parent?.BackColor ?? t.Background;
        g.Clear(back);
        var (glyph, color) = level switch
        {
            Level.Ok => (Glyph.Check, t.Success),
            Level.Warn => (Glyph.Warning, t.Warning),
            Level.Bad => (Glyph.Error, t.Danger),
            _ => (Glyph.Refresh, t.TextTertiary),
        };
        const TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, glyph, Fonts.IconSmall, new Rectangle(0, 0, S(16), Height), color, back, f | TextFormatFlags.HorizontalCenter);
        int x = S(22);
        var tw = TextRenderer.MeasureText(g, text, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding).Width;
        int actionW = action.Length > 0 ? TextRenderer.MeasureText(g, action, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding).Width + S(10) : 0;
        TextRenderer.DrawText(g, text, Fonts.Caption, new Rectangle(x, 0, Math.Min(tw + 2, Width - x - actionW), Height), t.TextSecondary, back, f | TextFormatFlags.EndEllipsis);
        if (actionW > 0)
        {
            using var font = hover ? new Font(Fonts.Caption, FontStyle.Underline) : null;
            TextRenderer.DrawText(g, action, font ?? Fonts.Caption, new Rectangle(Math.Min(x + tw + S(10), Width - actionW + S(10)), 0, actionW, Height), t.Accent, back, f);
        }
    }
}

/// <summary>Rounded warning (or error) banner with an icon and wrapped text.</summary>
internal sealed class Banner : Control
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool IsError { get; set; }
    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    Color Back(Theme t) => !IsError ? t.WarningBack : t.Dark ? Color.FromArgb(68, 39, 38) : Color.FromArgb(253, 231, 233);
    Color Fore(Theme t) => IsError ? (t.Dark ? Color.FromArgb(255, 200, 200) : Color.FromArgb(150, 20, 20)) : t.Dark ? t.Text : Color.FromArgb(80, 55, 0);

    public Banner()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.Alert;
    }

    /// <summary>Sets the text and resizes to fit the given width.</summary>
    public void SetText(string text, int width)
    {
        Text = text;
        AccessibleName = text;
        var sz = TextRenderer.MeasureText(text, Fonts.Caption, new Size(width - S(46), 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        Size = new Size(width, sz.Height + S(20));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var back = Back(t);
        Theme.FillRounded(g, back, r, S(6));
        TextRenderer.DrawText(g, IsError ? Glyph.Error : Glyph.Warning, Fonts.Icon, new Rectangle(S(12), S(10), S(18), S(18)),
            IsError ? t.Danger : t.Warning, back, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Text, Fonts.Caption, new Rectangle(S(36), S(10), Width - S(46), Height - S(20)),
            Fore(t), back, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// Windows 11-style text box: rounded field, subtle border, accent underline while focused, placeholder.
/// The real TextBox is hosted inside (so editing, IME and accessibility are standard).
/// </summary>
internal sealed class FluentTextBox : Control
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None };
    bool hover;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public FluentTextBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Box.Font = Fonts.Body;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Box.MouseEnter += (_, _) => { hover = true; Invalidate(); };
        Box.MouseLeave += (_, _) => { hover = false; Invalidate(); };
        Cursor = Cursors.IBeam;
        ApplyTheme();
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text { get => Box.Text; set => Box.Text = value ?? ""; }

    public void ApplyTheme()
    {
        var t = Theme.Current;
        Box.BackColor = FieldColor(t);
        Box.ForeColor = Box.ReadOnly ? t.TextSecondary : t.Text;
        Invalidate();
    }

    Color FieldColor(Theme t) => Box.ReadOnly
        ? (t.Dark ? Color.FromArgb(39, 39, 39) : Color.FromArgb(246, 246, 246))
        : Box.Focused ? (t.Dark ? Color.FromArgb(31, 31, 31) : Color.White)
        : hover ? t.SubtleHover : t.Subtle;

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int h = Box.PreferredHeight;
        Box.Bounds = new Rectangle(S(10), (Height - h) / 2 + S(1), Width - S(20), h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = FieldColor(t);
        if (Box.BackColor != fill) Box.BackColor = fill;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Theme.FillRounded(g, fill, r, S(5));
        Theme.StrokeRounded(g, t.Dark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(222, 222, 222), r, S(5));
        if (Box.Focused && !Box.ReadOnly)
        {
            // Accent underline, clipped to the rounded field.
            using var clip = Theme.Rounded(r, S(5));
            g.SetClip(clip);
            using var b = new SolidBrush(t.Accent);
            g.FillRectangle(b, 0, Height - S(2), Width, S(2));
            g.ResetClip();
        }
    }
}

/// <summary>Round color swatch (selected: ring + check), or the "+" custom-color button.</summary>
internal sealed class SwatchButton : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Color Swatch { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Selected { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool IsAdd { get; set; }
    bool hover;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public SwatchButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = S(4);
        var r = new RectangleF(inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
        if (IsAdd)
        {
            using var pen = new Pen(hover ? t.Accent : t.TextTertiary, 1.2f) { DashStyle = DashStyle.Dash };
            g.DrawEllipse(pen, r);
            TextRenderer.DrawText(g, Glyph.Add, Fonts.IconSmall, ClientRectangle, hover ? t.Accent : t.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            using (var b = new SolidBrush(Swatch)) g.FillEllipse(b, r);
            if (Selected || hover)
            {
                using var ring = new Pen(Selected ? t.Text : t.TextTertiary, Selected ? 2f : 1.2f);
                g.DrawEllipse(ring, 1.5f, 1.5f, Width - 4f, Height - 4f);
            }
            if (Selected)
            {
                var lum = (0.299 * Swatch.R + 0.587 * Swatch.G + 0.114 * Swatch.B) / 255.0;
                TextRenderer.DrawText(g, Glyph.Check, Fonts.IconSmall, ClientRectangle, lum > 0.6 ? Color.Black : Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(t.Accent, 1.5f) { DashStyle = DashStyle.Dot };
            g.DrawEllipse(focus, 0.75f, 0.75f, Width - 2.5f, Height - 2.5f);
        }
    }
}

/// <summary>Square icon choice (selected: accent border and tint).</summary>
internal sealed class IconTile : Button
{
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public Bitmap? Picture { get; set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Selected { get; set; }
    bool hover;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public IconTile()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? t.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        var fill = Selected ? Theme.Mix(t.Card, t.Accent, t.Dark ? 0.22 : 0.12) : hover ? t.SubtleHover : t.Card;
        Theme.FillRounded(g, fill, r, S(7));
        if (Selected) Theme.StrokeRounded(g, t.Accent, new RectangleF(1, 1, Width - 2.5f, Height - 2.5f), S(7), 2f);
        else Theme.StrokeRounded(g, t.CardBorder, r, S(7));
        if (Picture != null)
        {
            int s = S(24);
            g.DrawImage(Picture, (Width - s) / 2, (Height - s) / 2, s, s);
        }
        if (Focused && ShowFocusCues)
            Theme.StrokeRounded(g, t.Text, new RectangleF(2.5f, 2.5f, Width - 6f, Height - 6f), S(5), 1f);
    }
}
