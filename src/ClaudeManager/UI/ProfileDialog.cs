using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>
/// Add / edit a profile. Windows 11 look that follows light/dark mode: a live preview card at the top,
/// then name, color, icon and folders, with inline validation.
/// </summary>
internal sealed class ProfileDialog : Form
{
    public static readonly string[] Palette = { "#D97757", "#2F80ED", "#27AE60", "#9B51E0", "#EB5757", "#F2994A", "#00A3A3", "#828282" };
    public static readonly char[] BadNameChars = "\\/:*?\"<>|&%^;!".ToCharArray();

    readonly AppState state;
    readonly Profile? existing;
    readonly bool isDefault;

    readonly FluentTextBox nameBox = new();
    readonly FluentTextBox desktopBox = new();
    readonly FluentTextBox cliBox = new();
    readonly FluentButton desktopBrowse = new() { Text = "Browse", GlyphText = Glyph.Folder };
    readonly FluentButton cliBrowse = new() { Text = "Browse", GlyphText = Glyph.Folder };
    readonly FluentButton chooseImage = new() { Text = "Choose image…", GlyphText = Glyph.Folder };
    readonly FluentButton ok = new() { Kind = ButtonKind.Accent };
    readonly FluentButton cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
    readonly List<SwatchButton> swatches = new();
    readonly List<IconTile> tiles = new();
    readonly Banner error = new() { IsError = true, Visible = false };
    readonly ToolTip tips = new();
    readonly List<Label> labels = new();
    Label? note;

    /// <summary>Custom images imported in this dialog session (deleted if not kept).</summary>
    readonly List<string> importedThisSession = new();
    bool pathsTouched, settingPaths;
    Rectangle previewRect;
    int footerTop;

    public string ProfileName => nameBox.Text.Trim();
    public string ProfileColor { get; private set; }
    public string ProfileIcon { get; private set; }
    public string DesktopDir => desktopBox.Text.Trim();
    public string CliDir => cliBox.Text.Trim();

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    int W => S(548);
    int Pad => S(24);

    public ProfileDialog(AppState state, Profile? existing)
    {
        this.state = state;
        this.existing = existing;
        isDefault = existing?.IsDefault == true;
        ProfileColor = existing?.Color ?? Palette[state.Profiles.Count % Palette.Length];
        ProfileIcon = existing?.Icon ?? IconFactory.ClaudeSpec;

        Text = existing == null ? "Add profile" : $"Edit profile · {existing.Name}";
        Font = Fonts.Body;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true; // Esc = CancelButton, Enter = AcceptButton
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        // Fields
        nameBox.Box.PlaceholderText = "For example Work, Personal, Client A";
        nameBox.Box.MaxLength = 40;
        nameBox.Text = existing?.Name ?? "";
        nameBox.Box.TextChanged += (_, _) =>
        {
            if (existing == null && !pathsTouched) Suggest();
            RefreshIconTiles();   // "Initial letter" follows the name
            Invalidate(previewRect);
            HideError();
        };
        desktopBox.Box.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; HideError(); };
        cliBox.Box.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; HideError(); };

        if (isDefault)
        {
            desktopBox.Text = Paths.DefaultDesktopDir;
            cliBox.Text = Paths.ExistingCliDir;
            desktopBox.Box.ReadOnly = cliBox.Box.ReadOnly = true;
            desktopBrowse.Visible = cliBrowse.Visible = false;
        }
        else if (existing != null)
        {
            desktopBox.Text = existing.DesktopDataDir ?? "";
            cliBox.Text = existing.CliConfigDir ?? "";
            pathsTouched = true;
        }
        else Suggest();

        desktopBrowse.Click += (_, _) => Browse(desktopBox, "Choose a dedicated folder for this profile's desktop app data");
        cliBrowse.Click += (_, _) => Browse(cliBox, "Choose a dedicated folder for this profile's Claude Code settings");
        chooseImage.Click += (_, _) => ChooseImage();
        tips.SetToolTip(chooseImage, "Use your own .ico, .png, .jpg, or the icon of an .exe");

        ok.Text = existing == null ? "Create profile" : "Save";
        ok.Click += (_, _) =>
        {
            var e = Validate();
            if (e != null) { ShowError(e); return; }
            DialogResult = DialogResult.OK;
        };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange(new Control[] { nameBox, desktopBox, cliBox, desktopBrowse, cliBrowse, chooseImage, error, ok, cancel });
        BuildSwatches();
        BuildIconTiles();
        ApplyTheme();
        LayoutAll();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyChrome(this, Theme.Current); // dark title bar in dark mode
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        nameBox.Box.Focus();
        nameBox.Box.SelectionStart = nameBox.Box.TextLength;
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutAll();
    }

    void ApplyTheme()
    {
        var t = Theme.Current;
        BackColor = t.Background;
        ForeColor = t.Text;
        foreach (var b in new[] { desktopBrowse, cliBrowse, chooseImage, ok, cancel }) b.Surface = t.Background;
        foreach (var f in new[] { nameBox, desktopBox, cliBox }) f.ApplyTheme();
    }

    // ---------- layout ----------

    Label MakeLabel(string text, Font font, Color color, int x, int y, int maxWidth = 0)
    {
        var l = new Label
        {
            Text = text, Font = font, ForeColor = color, BackColor = Theme.Current.Background, AutoSize = true,
            Location = new Point(x, y), MaximumSize = new Size(maxWidth, 0), UseMnemonic = false,
        };
        labels.Add(l);
        Controls.Add(l);
        return l;
    }

    void LayoutAll()
    {
        SuspendLayout();
        foreach (var l in labels) { Controls.Remove(l); l.Dispose(); }
        labels.Clear();
        var t = Theme.Current;
        int pad = Pad, inner = W - 2 * pad, field = S(34), y = S(20);

        previewRect = new Rectangle(pad, y, inner, S(84));
        y += previewRect.Height + S(22);

        MakeLabel("Name", Fonts.BodyStrong, t.Text, pad, y); y += S(24);
        nameBox.Bounds = new Rectangle(pad, y, inner, field); y += field + S(20);

        MakeLabel("Color", Fonts.BodyStrong, t.Text, pad, y); y += S(24);
        int sx = pad - S(3);
        foreach (var s in swatches) { s.Bounds = new Rectangle(sx, y, S(34), S(34)); sx += S(38); }
        y += S(34) + S(18);

        MakeLabel("Icon", Fonts.BodyStrong, t.Text, pad, y + S(4));
        int cw = Math.Max(chooseImage.PreferredWidth(), S(130));
        chooseImage.Bounds = new Rectangle(W - pad - cw, y, cw, S(30));
        y += S(38);
        int tile = S(42), gap = S(7), perRow = Math.Max(1, (inner + gap) / (tile + gap));
        for (int i = 0; i < tiles.Count; i++)
            tiles[i].Bounds = new Rectangle(pad + (i % perRow) * (tile + gap), y + (i / perRow) * (tile + gap), tile, tile);
        y += ((tiles.Count + perRow - 1) / perRow) * (tile + gap) + S(12);

        // Divider before folders
        y += S(4);
        int dividerY = y;
        y += S(16);
        MakeLabel("Folders", Fonts.BodyStrong, t.Text, pad, y); y += S(24);
        MakeLabel(isDefault ? "Desktop app data (app default)" : "Desktop app data", Fonts.Caption, t.TextSecondary, pad, y); y += S(18);
        int bw = isDefault ? 0 : Math.Max(desktopBrowse.PreferredWidth(), S(96));
        desktopBox.Bounds = new Rectangle(pad, y, inner - (bw > 0 ? bw + S(8) : 0), field);
        if (bw > 0) desktopBrowse.Bounds = new Rectangle(W - pad - bw, y, bw, field);
        y += field + S(12);
        MakeLabel("Claude Code", Fonts.Caption, t.TextSecondary, pad, y); y += S(18);
        cliBox.Bounds = new Rectangle(pad, y, inner - (bw > 0 ? bw + S(8) : 0), field);
        if (bw > 0) cliBrowse.Bounds = new Rectangle(W - pad - bw, y, bw, field);
        y += field + S(10);

        var noteText = isDefault
            ? "This is your existing Claude setup. Its folders can't be changed, and Claude Manager never writes to them."
            : existing != null
                ? "Changing a folder starts that app fresh (a new sign-in). The old folder stays on disk."
                : "Each folder holds this account's own login, history and settings. Folders that already exist are reused as they are.";
        note = MakeLabel(noteText, Fonts.Caption, t.TextTertiary, pad, y, inner);
        y += note.PreferredHeight + S(14);

        if (error.Visible)
        {
            error.SetText(error.Text, inner);
            error.Location = new Point(pad, y);
            y += error.Height + S(14);
        }

        footerTop = y;
        int bh = S(32);
        int okW = Math.Max(ok.PreferredWidth(), S(120)), cancelW = Math.Max(cancel.PreferredWidth(), S(96));
        int by = footerTop + (S(64) - bh) / 2;
        ok.Bounds = new Rectangle(W - pad - okW, by, okW, bh);
        cancel.Bounds = new Rectangle(ok.Left - S(8) - cancelW, by, cancelW, bh);
        ClientSize = new Size(W, footerTop + S(64));
        dividerLineY = dividerY;
        ResumeLayout();
        Invalidate();
    }

    int dividerLineY;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var t = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

        // Live preview card: how the profile will look in the panel.
        var r = new RectangleF(previewRect.X + 0.5f, previewRect.Y + 0.5f, previewRect.Width - 1, previewRect.Height - 1);
        Theme.FillRounded(g, t.Card, r, S(10));
        Theme.StrokeRounded(g, t.CardBorder, r, S(10));
        // Color accent strip on the left edge.
        using (var clip = Theme.Rounded(r, S(10)))
        {
            g.SetClip(clip);
            using var strip = new SolidBrush(IconFactory.Parse(ProfileColor));
            g.FillRectangle(strip, previewRect.X, previewRect.Y, S(5), previewRect.Height);
            g.ResetClip();
        }
        int icon = S(48);
        var img = IconFactory.Image(ProfileIcon, ProfileColor, ProfileName, icon);
        g.DrawImage(img, previewRect.X + S(20), previewRect.Y + (previewRect.Height - icon) / 2, icon, icon);
        int tx = previewRect.X + S(20) + icon + S(16);
        var name = ProfileName.Length > 0 ? ProfileName : existing == null ? "New profile" : existing.Name;
        TextRenderer.DrawText(g, name, Fonts.Title, new Rectangle(tx, previewRect.Y + S(18), previewRect.Right - tx - S(16), S(26)),
            ProfileName.Length > 0 ? t.Text : t.TextTertiary, t.Card, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var sub = isDefault ? "Your existing setup · opens Claude exactly as today"
            : $"Claude ({name})  ·  Claude Code ({name})";
        TextRenderer.DrawText(g, sub, Fonts.Caption, new Rectangle(tx, previewRect.Y + S(48), previewRect.Right - tx - S(16), S(18)),
            t.TextSecondary, t.Card, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        using (var pen = new Pen(t.Divider))
        {
            g.DrawLine(pen, Pad, dividerLineY, W - Pad, dividerLineY);
            // Footer band
            using var band = new SolidBrush(t.Dark ? Color.FromArgb(27, 27, 27) : Color.FromArgb(236, 236, 236));
            g.FillRectangle(band, 0, footerTop, ClientSize.Width, ClientSize.Height - footerTop);
            g.DrawLine(pen, 0, footerTop, ClientSize.Width, footerTop);
        }
        var bandColor = t.Dark ? Color.FromArgb(27, 27, 27) : Color.FromArgb(236, 236, 236);
        if (ok.Surface != bandColor) { ok.Surface = cancel.Surface = bandColor; ok.Invalidate(); cancel.Invalidate(); }
    }

    // ---------- color ----------

    void BuildSwatches()
    {
        foreach (var s in swatches) { Controls.Remove(s); s.Dispose(); }
        swatches.Clear();
        var colors = Palette.ToList();
        if (!colors.Contains(ProfileColor, StringComparer.OrdinalIgnoreCase)) colors.Add(ProfileColor);
        foreach (var hex in colors)
        {
            var s = new SwatchButton { Swatch = IconFactory.Parse(hex), Selected = hex.Equals(ProfileColor, StringComparison.OrdinalIgnoreCase), AccessibleName = $"Color {hex}" };
            var chosen = hex;
            s.Click += (_, _) => SetColor(chosen);
            tips.SetToolTip(s, hex);
            swatches.Add(s);
        }
        var add = new SwatchButton { IsAdd = true, AccessibleName = "Custom color" };
        add.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = IconFactory.Parse(ProfileColor), FullOpen = true };
            if (cd.ShowDialog(this) == DialogResult.OK) { ProfileColor = IconFactory.ToHex(cd.Color); BuildSwatches(); LayoutAll(); RefreshIconTiles(); }
        };
        tips.SetToolTip(add, "Custom color…");
        swatches.Add(add);
        Controls.AddRange(swatches.ToArray<Control>());
    }

    void SetColor(string hex)
    {
        ProfileColor = hex;
        foreach (var s in swatches) { s.Selected = !s.IsAdd && IconFactory.ToHex(s.Swatch).Equals(IconFactory.ToHex(IconFactory.Parse(hex)), StringComparison.OrdinalIgnoreCase); s.Invalidate(); }
        RefreshIconTiles(); // presets and the Claude badge use the profile color
        Invalidate(previewRect);
    }

    // ---------- icon ----------

    List<string> IconSpecs()
    {
        var specs = new List<string> { IconFactory.ClaudeSpec };
        specs.AddRange(IconFactory.Presets.Select(p => IconFactory.PresetPrefix + p.Key));
        if (ProfileIcon.StartsWith(IconFactory.FilePrefix)) specs.Add(ProfileIcon);
        return specs;
    }

    void BuildIconTiles()
    {
        foreach (var t in tiles) { Controls.Remove(t); t.Dispose(); } // pictures are cached/shared, not owned
        tiles.Clear();
        foreach (var spec in IconSpecs())
        {
            var tile = new IconTile { Tag = spec, AccessibleName = IconFactory.Describe(spec) };
            var chosen = spec;
            tile.Click += (_, _) => { ProfileIcon = chosen; RefreshIconTiles(); Invalidate(previewRect); };
            tips.SetToolTip(tile, IconFactory.Describe(spec) + (spec == IconFactory.ClaudeSpec ? " (default): Claude's icon with a dot in the profile color" : ""));
            tiles.Add(tile);
        }
        Controls.AddRange(tiles.ToArray<Control>());
        RefreshIconTiles();
    }

    void RefreshIconTiles()
    {
        foreach (var tile in tiles)
        {
            var spec = (string)tile.Tag!;
            tile.Picture = IconFactory.Image(spec, ProfileColor, nameBox.Text, S(24));
            tile.Selected = spec == ProfileIcon;
            tile.Invalidate();
        }
    }

    void ChooseImage()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Choose an icon or image for this profile",
            Filter = string.Join("|", IconFactory.ImportFilter),
            CheckFileExists = true,
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var spec = IconFactory.ImportCustom(ofd.FileName);
            importedThisSession.Add(spec);
            ProfileIcon = spec;
            BuildIconTiles();
            LayoutAll();
        }
        catch (Exception ex) { ShowError($"That image could not be used: {ex.Message}"); }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Remove imported images that weren't kept (cancelled, or another icon chosen afterwards).
        var kept = DialogResult == DialogResult.OK ? ProfileIcon : existing?.Icon;
        foreach (var spec in importedThisSession.Where(s => s != kept))
            try { File.Delete(spec[IconFactory.FilePrefix.Length..]); } catch { }
        tips.Dispose();
        base.OnFormClosed(e);
    }

    // ---------- folders ----------

    void Suggest()
    {
        var name = ProfileName.Length == 0 ? "profile" : ProfileName;
        var (d, c) = ProfileService.SuggestFolders(state, name, state.Layout, existing);
        settingPaths = true;
        desktopBox.Text = d;
        cliBox.Text = c;
        settingPaths = false;
    }

    void Browse(FluentTextBox target, string description)
    {
        using var fb = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = description, ShowNewFolderButton = true };
        try { var parent = Path.GetDirectoryName(Paths.Normalize(target.Text)); if (Directory.Exists(parent)) fb.InitialDirectory = parent; } catch { }
        if (fb.ShowDialog(this) == DialogResult.OK) target.Text = fb.SelectedPath;
    }

    // ---------- validation ----------

    void ShowError(string message)
    {
        error.Text = message;
        error.Visible = true;
        LayoutAll();
        System.Media.SystemSounds.Beep.Play();
    }

    void HideError()
    {
        if (!error.Visible) return;
        error.Visible = false;
        LayoutAll();
    }

    new string? Validate()
    {
        var n = ProfileName;
        if (n.Length == 0) return "Enter a name.";
        if (n.Length > 40) return "Keep the name under 40 characters.";
        if (n.IndexOfAny(BadNameChars) >= 0) return "The name can't contain \\ / : * ? \" < > | & % ^ ; !";
        if (state.Profiles.Any(p => !ReferenceEquals(p, existing) && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return $"A profile named \"{n}\" already exists.";
        if (isDefault) return null;

        if (DesktopDir.Length == 0 || CliDir.Length == 0) return "Both folders are required.";
        if (SafetyGuard.CheckProfileFolder(DesktopDir) is { } e1) return $"Desktop data folder {e1}";
        if (SafetyGuard.CheckProfileFolder(CliDir) is { } e2) return $"Claude Code folder {e2}";
        if (SafetyGuard.IsSameOrInside(DesktopDir, CliDir) || SafetyGuard.IsSameOrInside(CliDir, DesktopDir))
            return "Use separate folders for the desktop app and Claude Code.";
        if (ProfileService.OverlappingProfile(state, DesktopDir, existing) is { } o1)
            return $"The desktop data folder is the same as, inside, or contains a folder of profile \"{o1.Name}\". Pick a separate folder.";
        if (ProfileService.OverlappingProfile(state, CliDir, existing) is { } o2)
            return $"The Claude Code folder is the same as, inside, or contains a folder of profile \"{o2.Name}\". Pick a separate folder.";
        return null;
    }
}
