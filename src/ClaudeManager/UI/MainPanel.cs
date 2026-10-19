using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeManager.Core;
using Microsoft.Win32;

namespace ClaudeManager.UI;

/// <summary>
/// The tray flyout (Windows 11 style): follows light/dark mode and the accent color, rounded corners,
/// one card per profile with live status, and auto-hides when you click elsewhere (unless pinned).
/// </summary>
internal sealed class MainPanel : Form
{
    readonly TrayApp app;
    readonly FluentButton refreshBtn = new() { GlyphText = Glyph.Refresh, Kind = ButtonKind.Subtle, AccessibleName = "Recheck" };
    readonly FluentButton settingsBtn = new() { GlyphText = Glyph.Settings, Kind = ButtonKind.Subtle, AccessibleName = "Settings" };
    readonly FluentButton pinBtn = new() { GlyphText = Glyph.Pin, Kind = ButtonKind.Subtle, AccessibleName = "Keep open" };
    readonly FluentButton closeBtn = new() { GlyphText = Glyph.Close, Kind = ButtonKind.Subtle, AccessibleName = "Close" };
    readonly StatusChip desktopChip = new();
    readonly StatusChip cliChip = new();
    readonly Banner warning = new() { Visible = false };
    readonly Panel list = new() { AutoScroll = false };
    readonly FluentButton addBtn = new() { Text = "Add profile", GlyphText = Glyph.Add, Kind = ButtonKind.Dashed };
    readonly Dictionary<string, ProfileCard> cards = new();
    readonly ContextMenuStrip popup = new();
    readonly ToolTip tips = new();
    readonly ToolTip cardTips = new(); // reset on every rebuild so old cards can be collected
    readonly System.Windows.Forms.Timer fade = new() { Interval = 15 };

    UsageSnapshot? lastUsage;
    string summary = "";
    int sectionY;
    bool pinned;

    /// <summary>When the flyout last hid itself; a tray click right after must not reopen it.</summary>
    public DateTime LastAutoHideUtc { get; private set; }

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    int PanelWidth => S(384);
    int Pad => S(14);

    public MainPanel(TrayApp app)
    {
        this.app = app;
        // No caption: Text empty + no control box. Keeps the DWM frame, so Windows 11 draws the rounded
        // corners, the 1 px border and the shadow; Windows 10 shows a thin border.
        Text = "";
        AccessibleName = "Claude Manager";
        ControlBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        DoubleBuffered = true;
        Font = Fonts.Body;
        AutoScaleMode = AutoScaleMode.None; // everything is laid out in device pixels via S()

        foreach (var b in new[] { refreshBtn, settingsBtn, pinBtn, closeBtn }) Controls.Add(b);
        Controls.AddRange(new Control[] { desktopChip, cliChip, warning, list });
        list.Controls.Add(addBtn);

        tips.SetToolTip(refreshBtn, "Look for the Claude desktop app and Claude Code again");
        tips.SetToolTip(settingsBtn, "Settings");
        tips.SetToolTip(pinBtn, "Keep this panel open");
        tips.SetToolTip(closeBtn, "Close (Esc)");

        refreshBtn.Click += (_, _) => app.StartDetection();
        settingsBtn.Click += (_, _) => ShowSettingsMenu(settingsBtn);
        pinBtn.Click += (_, _) => SetPinned(!pinned);
        closeBtn.Click += (_, _) => Hide();
        addBtn.Click += (_, _) => app.AddProfile();

        fade.Tick += (_, _) =>
        {
            Opacity = Math.Min(1.0, Opacity + 0.2);
            if (Opacity >= 1.0) fade.Stop();
        };

        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        Deactivate += (_, _) => OnDeactivated();
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        ApplyTheme();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged; // static event: must unsubscribe
            fade.Dispose();
            tips.Dispose();
            cardTips.Dispose();
            popup.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyChrome(this, Theme.Current);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rebuild();
    }

    // ---------- theme ----------

    void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Accessibility)
            if (Theme.Refresh()) { ApplyTheme(); Rebuild(); }
    }

    void ApplyTheme()
    {
        var t = Theme.Current;
        BackColor = t.Background;
        ForeColor = t.Text;
        list.BackColor = t.Background;
        foreach (var b in new[] { refreshBtn, settingsBtn, pinBtn, closeBtn, addBtn }) { b.Surface = t.Background; b.Invalidate(); }
        Theme.ApplyChrome(this, t);
        if (IsHandleCreated) DarkScrollbars(list, t.Dark);
        Invalidate(true);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string? app, string? id);
    static void DarkScrollbars(Control c, bool dark) { try { SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }

    // ---------- show / hide ----------

    public void ShowNearTray()
    {
        Rebuild();
        Place();
        bool wasVisible = Visible;
        if (!wasVisible) Opacity = 0;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Place();
        Activate();
        BringToFront();
        if (!wasVisible) fade.Start();
    }

    void Place()
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        var b = screen.Bounds;
        int gap = S(12);
        // Open next to the taskbar, wherever it is.
        int x = wa.Left > b.Left ? wa.Left + gap : wa.Right - Width - gap;
        int y = wa.Top > b.Top ? wa.Top + gap : wa.Bottom - Height - gap;
        Location = new Point(x, y);
    }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

    async void OnDeactivated()
    {
        if (pinned) return;
        await Task.Delay(80); // let the new foreground window settle
        if (!Visible || pinned || ContainsFocus) return;
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid == Environment.ProcessId) return; // our own dialog or menu: stay open behind it
        LastAutoHideUtc = DateTime.UtcNow;
        Hide();
    }

    void SetPinned(bool on)
    {
        pinned = on;
        pinBtn.GlyphText = on ? Glyph.Pinned : Glyph.Pin;
        pinBtn.Kind = on ? ButtonKind.Standard : ButtonKind.Subtle;
        tips.SetToolTip(pinBtn, on ? "Unpin (hide when I click elsewhere)" : "Keep this panel open");
        pinBtn.Invalidate();
    }

    // ---------- content ----------

    public void Rebuild()
    {
        if (IsDisposed) return;
        SuspendLayout();
        UpdateStatus();

        var vars = ClaudeCli.OverridingVars();
        warning.Visible = vars.Count > 0;
        if (warning.Visible)
            warning.SetText($"{string.Join(", ", vars)} is set. It takes priority over a profile's login in Claude Code; you'll be asked when opening a terminal.",
                PanelWidth - 2 * Pad);

        // Cards (recreated: profiles may have changed). Images are shared/cached and not owned by the cards.
        cardTips.RemoveAll();
        foreach (var c in cards.Values) { list.Controls.Remove(c); c.Dispose(); }
        cards.Clear();
        foreach (var p in app.State.Profiles)
        {
            var card = new ProfileCard(p);
            var profile = p;
            card.Desktop.Click += (_, _) => app.LaunchDesktop(profile);
            card.Terminal.Click += (_, _) => ShowTerminalMenu(profile, card.Terminal);
            card.More.Click += (_, _) => ShowMoreMenu(profile, card.More);
            cardTips.SetToolTip(card, Details(p));
            cardTips.SetToolTip(card.Desktop, $"Open {p.DesktopTitle}");
            cardTips.SetToolTip(card.Terminal, $"Open {p.TerminalTitle} in a project folder");
            cardTips.SetToolTip(card.More, "Edit, shortcuts, folders, delete…");
            cards[p.Id] = card;
            list.Controls.Add(card);
        }
        addBtn.Text = app.State.Profiles.Count == 0 ? "Add your first profile" : "Add profile";

        LayoutAll();
        ResumeLayout();
        UpdateUsage(app.State.ShowMemory ? lastUsage : null);
        if (Visible) Place();
        Invalidate();
    }

    void UpdateStatus()
    {
        var d = app.Desktop;
        if (d == null) desktopChip.Set(StatusChip.Level.Checking, "Looking for the Claude desktop app…");
        else if (d.Kind == DesktopKind.NotFound) desktopChip.Set(StatusChip.Level.Bad, "Claude desktop app not installed", "Download", () => Dialogs.OpenUrl(DesktopApp.DownloadUrl));
        else desktopChip.Set(StatusChip.Level.Ok, $"Claude desktop {d.Version ?? ""}{(d.Kind == DesktopKind.Packaged ? " · Microsoft Store" : "")}".Trim());
        tips.SetToolTip(desktopChip, d?.Describe() ?? "");

        var c = app.Cli;
        if (c == null) cliChip.Set(StatusChip.Level.Checking, "Looking for Claude Code…");
        else if (!c.Found) cliChip.Set(StatusChip.Level.Bad, "Claude Code not installed", "Install", () => Dialogs.OpenUrl(ClaudeCli.InstallUrl));
        else if (c.TooOld) cliChip.Set(StatusChip.Level.Warn, $"Claude Code {c.Version} is too old", "Update", app.RunCliUpdate);
        else if (c.Unverified) cliChip.Set(StatusChip.Level.Warn, "Claude Code version couldn't be checked", "Check again", app.StartDetection);
        else cliChip.Set(StatusChip.Level.Ok, $"Claude Code {c.Version}");
        tips.SetToolTip(cliChip, c?.Path ?? "");
    }

    void LayoutAll()
    {
        int w = PanelWidth, pad = Pad, inner = w - 2 * pad;
        int btn = S(32);

        // Header: title (painted) + icon buttons on the right.
        int hx = w - pad + S(4);
        foreach (var b in new[] { closeBtn, pinBtn, settingsBtn, refreshBtn })
        {
            hx -= btn;
            b.Bounds = new Rectangle(hx, S(10), btn, btn);
            hx -= S(2);
        }

        int y = S(54);
        desktopChip.Bounds = new Rectangle(pad, y, inner, S(20)); y += S(22);
        cliChip.Bounds = new Rectangle(pad, y, inner, S(20)); y += S(28);
        if (warning.Visible) { warning.Location = new Point(pad, y); y += warning.Height + S(12); }

        sectionY = y; y += S(26); // "Profiles" + summary (painted)

        // Card list; scrolls when there are many profiles.
        int cardH = ProfileCard.CardHeight(DeviceDpi), gap = S(8), addH = S(40);
        int content = cards.Count * (cardH + gap) + addH;
        var workArea = Screen.FromPoint(Cursor.Position).WorkingArea.Height;
        int maxList = Math.Max(cardH * 2, Math.Min(cardH * 4 + gap * 4 + addH, workArea - y - S(120)));
        bool scroll = content > maxList;
        int listH = scroll ? maxList : content;
        int cardW = inner - (scroll ? SystemInformation.VerticalScrollBarWidth + S(4) : 0);
        list.AutoScroll = scroll;
        list.Bounds = new Rectangle(pad, y, inner, listH);
        int cy = 0;
        foreach (var card in cards.Values)
        {
            card.Bounds = new Rectangle(0, cy, cardW, cardH);
            cy += cardH + gap;
        }
        addBtn.Bounds = new Rectangle(0, cy, cardW, addH);
        addBtn.Surface = Theme.Current.Background;
        y += listH + S(10);

        ClientSize = new Size(w, y + S(26)); // footer note (painted)
    }

    /// <summary>Updates live status in place (no rebuild: no flicker, no allocations per tick).</summary>
    public void UpdateUsage(UsageSnapshot? usage)
    {
        if (IsDisposed) return;
        lastUsage = usage;
        foreach (var (id, card) in cards)
            card.SetUsage(usage != null && usage.ByProfile.TryGetValue(id, out var u) ? u : null);
        var text = usage == null || usage.RunningProfiles == 0 ? ""
            : $"{usage.RunningProfiles} running · {UsageMonitor.Format(usage.TotalBytes)}";
        if (text != summary)
        {
            summary = text;
            Invalidate(new Rectangle(0, sectionY, Width, S(26)));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var t = Theme.Current;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        int pad = Pad;

        // Header: app icon, title, "Unofficial" pill.
        int iconSize = S(22);
        if (Icon != null) g.DrawIcon(Icon, new Rectangle(pad, S(15), iconSize, iconSize));
        int tx = pad + iconSize + S(10);
        var titleSize = TextRenderer.MeasureText(g, "Claude Manager", Fonts.Title, Size.Empty, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, "Claude Manager", Fonts.Title, new Point(tx, S(26) - titleSize.Height / 2), t.Text, t.Background, TextFormatFlags.NoPadding);
        const string tag = "Unofficial";
        var tagSize = TextRenderer.MeasureText(g, tag, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding);
        var pill = new RectangleF(tx + titleSize.Width + S(8), S(26) - S(9), tagSize.Width + S(12), S(18));
        if (pill.Right < refreshBtn.Left - S(4))
        {
            Theme.StrokeRounded(g, t.TextTertiary, pill, S(9));
            TextRenderer.DrawText(g, tag, Fonts.Caption, Rectangle.Round(pill), t.TextSecondary, t.Background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Section header.
        TextRenderer.DrawText(g, "Profiles", Fonts.BodyStrong, new Rectangle(pad, sectionY, S(120), S(20)), t.Text, t.Background,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        if (summary.Length > 0)
        {
            var sz = TextRenderer.MeasureText(g, summary, Fonts.Caption, Size.Empty, TextFormatFlags.NoPadding);
            int dot = S(6), sx = Width - pad - sz.Width;
            using (var b = new SolidBrush(t.Success)) g.FillEllipse(b, sx - dot - S(6), sectionY + (S(20) - dot) / 2f, dot, dot);
            TextRenderer.DrawText(g, summary, Fonts.Caption, new Rectangle(sx, sectionY, sz.Width + 2, S(20)), t.TextSecondary, t.Background,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // Footer note.
        using (var pen = new Pen(t.Divider)) g.DrawLine(pen, pad, ClientSize.Height - S(28), Width - pad, ClientSize.Height - S(28));
        TextRenderer.DrawText(g, About.ShortDisclaimer.Replace("Not affiliated with, endorsed by, or supported by", "Not affiliated with"),
            Fonts.Caption, new Rectangle(0, ClientSize.Height - S(27), ClientSize.Width, S(26)), t.TextTertiary, t.Background,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    static string Details(Profile p) => p.IsDefault
        ? $"Desktop: {Paths.DefaultDesktopDir} (app default)\nClaude Code: {Paths.ExistingCliDir}\nNever modified by Claude Manager."
        : $"Desktop: {p.DesktopDataDir}\nClaude Code: {p.CliConfigDir}";

    // ---------- menus ----------

    void ShowPopup(Control anchor)
    {
        FluentMenuRenderer.Apply(popup);
        popup.Show(anchor, new Point(0, anchor.Height + S(4)));
    }

    void ShowTerminalMenu(Profile p, Control anchor)
    {
        MenuUtil.Clear(popup.Items);
        var recent = p.RecentDirs.Where(Directory.Exists).Take(8).ToList();
        if (recent.Count > 0)
        {
            popup.Items.Add(new ToolStripLabel("Recent folders") { ForeColor = Theme.Current.TextTertiary, Font = Fonts.Caption });
            foreach (var d in recent)
            {
                var dir = d;
                popup.Items.Add(Paths.Shorten(dir), null, (_, _) => app.LaunchTerminal(p, dir));
            }
            popup.Items.Add(new ToolStripSeparator());
        }
        popup.Items.Add($"Home folder ({Paths.Shorten(Paths.UserProfile)})", null, (_, _) => app.LaunchTerminal(p, Paths.UserProfile));
        popup.Items.Add("Choose folder…", null, (_, _) => app.PickFolderAndLaunchTerminal(p));
        ShowPopup(anchor);
    }

    void ShowMoreMenu(Profile p, Control anchor)
    {
        MenuUtil.Clear(popup.Items);
        popup.Items.Add("Edit name, icon, color, folders…", null, (_, _) => app.EditProfile(p));
        popup.Items.Add(new ToolStripSeparator());
        popup.Items.Add("Create Desktop shortcuts", null, (_, _) => app.CreateShortcuts(p, false));
        popup.Items.Add("Create Start-menu shortcuts", null, (_, _) => app.CreateShortcuts(p, true));
        popup.Items.Add(new ToolStripSeparator());
        popup.Items.Add("Open Claude Code folder", null, (_, _) => app.OpenFolder(p.IsDefault ? Paths.ExistingCliDir : p.CliConfigDir));
        popup.Items.Add("Open desktop data folder", null, (_, _) => app.OpenFolder(p.IsDefault ? Paths.DefaultDesktopDir : p.DesktopDataDir));
        if (!p.IsDefault)
        {
            popup.Items.Add(new ToolStripSeparator());
            if (p.DesktopSignedIn) popup.Items.Add("Redo first desktop sign-in", null, (_, _) => app.SetSignedIn(p, false));
            else popup.Items.Add("Mark desktop as signed in", null, (_, _) => app.SetSignedIn(p, true));
            popup.Items.Add(new ToolStripSeparator());
            var del = new ToolStripMenuItem("Delete profile…") { ForeColor = Theme.Current.Danger };
            del.Click += (_, _) => app.DeleteProfile(p);
            popup.Items.Add(del);
        }
        ShowPopup(anchor);
    }

    void ShowSettingsMenu(Control anchor)
    {
        MenuUtil.Clear(popup.Items);
        popup.Items.Add("Back up current Claude settings", null, (_, _) => app.BackupSettings());
        popup.Items.Add("Open backups folder", null, (_, _) => app.OpenFolder(Paths.BackupDir));
        popup.Items.Add(new ToolStripSeparator());

        var layout = new ToolStripMenuItem("New profile folders");
        var home = new ToolStripMenuItem("Next to user folder (.claude-name)") { Checked = app.State.Layout == FolderLayout.UserProfile };
        home.Click += (_, _) => app.SetLayout(FolderLayout.UserProfile);
        var parent = new ToolStripMenuItem("Inside .claude-accounts") { Checked = app.State.Layout == FolderLayout.AccountsParent };
        parent.Click += (_, _) => app.SetLayout(FolderLayout.AccountsParent);
        layout.DropDownItems.Add(home);
        layout.DropDownItems.Add(parent);
        FluentMenuRenderer.Apply(layout.DropDown);
        popup.Items.Add(layout);

        var clear = new ToolStripMenuItem("Always clear ANTHROPIC_API_KEY in terminals") { Checked = app.State.AlwaysClearApiKey };
        clear.Click += (_, _) => app.SetAlwaysClearApiKey(!app.State.AlwaysClearApiKey);
        popup.Items.Add(clear);
        var memory = new ToolStripMenuItem("Show memory usage") { Checked = app.State.ShowMemory };
        memory.Click += (_, _) => app.SetShowMemory(!app.State.ShowMemory);
        popup.Items.Add(memory);
        var label = new ToolStripMenuItem("Show profile name in Claude window titles") { Checked = app.State.LabelWindows };
        label.Click += (_, _) => app.SetLabelWindows(!app.State.LabelWindows);
        popup.Items.Add(label);
        var trayLabel = new ToolStripMenuItem("Show profile name on Claude tray icons") { Checked = app.State.LabelTrayIcons };
        trayLabel.Click += (_, _) => app.SetLabelTrayIcons(!app.State.LabelTrayIcons);
        popup.Items.Add(trayLabel);
        var links = new ToolStripMenuItem("Send claude:// links to the right profile") { Checked = app.State.RouteLinks };
        links.ToolTipText = "Sign-in links (including Google sign-in) go through Claude Manager instead of always opening the Default profile.";
        links.Click += (_, _) => app.SetRouteLinks(!app.State.RouteLinks);
        popup.Items.Add(links);
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupRegistration.IsEnabled() };
        startup.Click += (_, _) => app.SetStartWithWindows(!startup.Checked);
        popup.Items.Add(startup);
        popup.Items.Add(new ToolStripSeparator());
        popup.Items.Add("Open log folder", null, (_, _) => app.OpenFolder(Paths.LogDir));
        popup.Items.Add("About Claude Manager (unofficial)…", null, (_, _) => app.ShowAbout());
        popup.Items.Add("Exit Claude Manager", null, (_, _) => app.Exit());
        ShowPopup(anchor);
    }
}
