using System.Drawing.Drawing2D;
using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>The tray panel: one row per profile with Desktop and Terminal buttons.</summary>
internal sealed class MainPanel : Form
{
    readonly TrayApp app;
    readonly FlowLayoutPanel rows = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty };
    readonly LinkLabel desktopStatus = new() { AutoSize = true };
    readonly LinkLabel cliStatus = new() { AutoSize = true };
    readonly Label warning = new() { AutoSize = true, ForeColor = Color.DarkOrange };
    readonly Label usageSummary = new() { AutoSize = true, Visible = false };
    readonly Dictionary<string, (Label Label, string Normal)> subtitles = new();
    UsageSnapshot? lastUsage;
    readonly ContextMenuStrip popup = new();
    readonly ToolTip tips = new();      // static controls (footer)
    readonly ToolTip rowTips = new();   // per-row controls; reset on every rebuild so old rows can be collected
    readonly Font bold;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);
    static readonly int[] Columns = { 26, 260, 86, 86, 40 };
    int RowWidth => S(Columns.Sum());

    public MainPanel(TrayApp app)
    {
        this.app = app;
        Text = "Claude Manager";
        Font = SystemFonts.MessageBoxFont ?? Font;
        bold = new Font(Font, FontStyle.Bold);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(12));
        KeyPreview = true;

        var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, Location = new Point(S(12), S(12)) };
        root.Controls.Add(new Label { Text = "Claude accounts", AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold), Margin = new Padding(0, 0, 0, S(4)) });
        root.Controls.Add(desktopStatus);
        root.Controls.Add(cliStatus);
        warning.MaximumSize = new Size(RowWidth, 0);
        root.Controls.Add(warning);
        usageSummary.Margin = new Padding(0, S(4), 0, 0);
        root.Controls.Add(usageSummary);
        root.Controls.Add(new Label { AutoSize = false, Height = 2, Width = RowWidth, BorderStyle = BorderStyle.Fixed3D, Margin = new Padding(0, S(8), 0, S(6)) });
        root.Controls.Add(rows);

        var footer = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(10), 0, 0) };
        var add = new Button { Text = "Add profile", AutoSize = true };
        add.Click += (_, _) => app.AddProfile();
        var settings = new Button { Text = "Settings ▾", AutoSize = true };
        settings.Click += (_, _) => ShowSettingsMenu(settings);
        var recheck = new Button { Text = "Recheck", AutoSize = true };
        recheck.Click += (_, _) => app.StartDetection();
        tips.SetToolTip(recheck, "Look for the Claude desktop app and Claude Code again");
        footer.Controls.AddRange(new Control[] { add, settings, recheck });
        root.Controls.Add(footer);
        Controls.Add(root);

        desktopStatus.LinkClicked += (_, _) => (desktopStatus.Tag as Action)?.Invoke();
        cliStatus.LinkClicked += (_, _) => (cliStatus.Tag as Action)?.Invoke();
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
    }

    public void ShowNearTray()
    {
        Rebuild();
        PerformLayout();
        Place();
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Place();
        Activate();
        BringToFront();
    }

    void Place()
    {
        var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(wa.Right - Width - S(12), wa.Bottom - Height - S(12));
    }

    public void Rebuild()
    {
        var d = app.Desktop;
        if (d == null) SetStatus(desktopStatus, "Claude desktop: checking…", null, null);
        else if (d.Kind == DesktopKind.NotFound) SetStatus(desktopStatus, "✗ " + d.Describe(), "Download", () => Dialogs.OpenUrl(DesktopApp.DownloadUrl));
        else SetStatus(desktopStatus, "✓ " + d.Describe(), null, null);

        var c = app.Cli;
        if (c == null) SetStatus(cliStatus, "Claude Code: checking…", null, null);
        else if (!c.Found) SetStatus(cliStatus, "✗ " + c.Describe(), "Install", () => Dialogs.OpenUrl(ClaudeCli.InstallUrl));
        else if (c.TooOld) SetStatus(cliStatus, "⚠ " + c.Describe(), "Run claude update", app.RunCliUpdate);
        else if (c.Unverified) SetStatus(cliStatus, "⚠ " + c.Describe(), "Check again", app.StartDetection);
        else SetStatus(cliStatus, "✓ " + c.Describe(), null, null);

        var vars = ClaudeCli.OverridingVars();
        warning.Visible = vars.Count > 0;
        warning.Text = vars.Count == 0 ? "" :
            $"⚠ {string.Join(", ", vars)} is set. It takes priority over a profile's login in Claude Code; you'll be asked when opening a terminal.";

        rows.SuspendLayout();
        rowTips.RemoveAll();
        subtitles.Clear();
        foreach (var ctl in rows.Controls.Cast<Control>().ToList()) ctl.Dispose();
        foreach (var p in app.State.Profiles) rows.Controls.Add(BuildRow(p));
        if (app.State.Profiles.Count == 0)
            rows.Controls.Add(new Label { Text = "No profiles yet. Click Add profile.", AutoSize = true, ForeColor = SystemColors.GrayText });
        rows.ResumeLayout();
        UpdateUsage(app.State.ShowMemory ? lastUsage : null);
        if (Visible) Place();
    }

    /// <summary>Updates memory figures in place (no row rebuild, so no flicker and no allocations per tick).</summary>
    public void UpdateUsage(UsageSnapshot? usage)
    {
        if (IsDisposed) return;
        lastUsage = usage;
        bool show = usage != null;
        if (usageSummary.Visible != show) usageSummary.Visible = show;
        if (usage != null)
        {
            var text = usage.RunningProfiles == 0
                ? "No profiles running"
                : $"{usage.RunningProfiles} profile{(usage.RunningProfiles == 1 ? "" : "s")} running · {UsageMonitor.Format(usage.TotalBytes)} memory in total";
            if (usageSummary.Text != text) usageSummary.Text = text;
        }
        foreach (var (id, (label, normal)) in subtitles)
        {
            string text;
            Color color;
            if (usage != null && usage.ByProfile.TryGetValue(id, out var u)) { text = UsageMonitor.Describe(u); color = Color.ForestGreen; }
            else { text = normal; color = SystemColors.GrayText; }
            if (label.Text != text) label.Text = text;
            if (label.ForeColor != color) label.ForeColor = color;
        }
    }

    static void SetStatus(LinkLabel l, string text, string? linkText, Action? action)
    {
        l.Text = linkText == null ? text : $"{text}   {linkText}";
        l.LinkArea = linkText == null ? new LinkArea(0, 0) : new LinkArea(text.Length + 3, linkText.Length);
        l.Tag = action;
    }

    Control BuildRow(Profile p)
    {
        var t = new TableLayoutPanel { ColumnCount = 5, RowCount = 1, AutoSize = true, Margin = new Padding(0, S(2), 0, S(2)) };
        foreach (var w in Columns) t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(w)));
        int textW = S(Columns[1] - 6);

        // Shared cached image: PictureBox.Dispose doesn't dispose it.
        var dot = new PictureBox
        {
            Size = new Size(S(20), S(20)),
            Image = IconFactory.ProfileImage(p, S(20)),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Anchor = AnchorStyles.None,
            Margin = Padding.Empty,
        };

        int lineH = TextRenderer.MeasureText("Ag", bold).Height + S(1);
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = Padding.Empty, Anchor = AnchorStyles.Left };
        text.Controls.Add(new Label { Text = p.Name, AutoSize = false, AutoEllipsis = true, Width = textW, Height = lineH, Font = bold, Margin = Padding.Empty });
        var sub = new Label { Text = Subtitle(p), AutoSize = false, AutoEllipsis = true, Width = textW, Height = lineH, ForeColor = SystemColors.GrayText, Margin = Padding.Empty };
        subtitles[p.Id] = (sub, sub.Text);
        text.Controls.Add(sub);
        rowTips.SetToolTip(sub, Details(p));

        var desktop = MakeButton("Desktop");
        desktop.Click += (_, _) => app.LaunchDesktop(p);
        rowTips.SetToolTip(desktop, $"Open {p.DesktopTitle}");
        var term = MakeButton("Terminal");
        term.Click += (_, _) => ShowTerminalMenu(p, term);
        rowTips.SetToolTip(term, $"Open {p.TerminalTitle} in Windows Terminal");
        var more = MakeButton("⋯");
        more.Width = S(34);
        more.Click += (_, _) => ShowMoreMenu(p, more);

        t.Controls.Add(dot, 0, 0);
        t.Controls.Add(text, 1, 0);
        t.Controls.Add(desktop, 2, 0);
        t.Controls.Add(term, 3, 0);
        t.Controls.Add(more, 4, 0);
        return t;
    }

    Button MakeButton(string text) =>
        new() { Text = text, Width = S(80), Height = S(28), Anchor = AnchorStyles.None, Margin = new Padding(S(2)) };

    static string Subtitle(Profile p)
    {
        if (p.IsDefault) return "Your existing setup (unchanged)";
        if (!p.DesktopSignedIn) return "Desktop: first sign-in needed";
        return Paths.Shorten(p.CliConfigDir ?? "");
    }

    static string Details(Profile p) => p.IsDefault
        ? $"Desktop: {Paths.DefaultDesktopDir} (app default)\nClaude Code: {Paths.ExistingCliDir}\nNever modified by Claude Manager."
        : $"Desktop: {p.DesktopDataDir}\nClaude Code: {p.CliConfigDir}";

    void ShowTerminalMenu(Profile p, Control anchor)
    {
        MenuUtil.Clear(popup.Items);
        foreach (var d in p.RecentDirs.Where(Directory.Exists).Take(8))
        {
            var dir = d;
            popup.Items.Add(Paths.Shorten(dir), null, (_, _) => app.LaunchTerminal(p, dir));
        }
        if (popup.Items.Count > 0) popup.Items.Add(new ToolStripSeparator());
        popup.Items.Add($"Home folder ({Paths.Shorten(Paths.UserProfile)})", null, (_, _) => app.LaunchTerminal(p, Paths.UserProfile));
        popup.Items.Add("Choose folder…", null, (_, _) => app.PickFolderAndLaunchTerminal(p));
        popup.Show(anchor, new Point(0, anchor.Height));
    }

    void ShowMoreMenu(Profile p, Control anchor)
    {
        MenuUtil.Clear(popup.Items);
        popup.Items.Add("Edit name, color, folders…", null, (_, _) => app.EditProfile(p));
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
            popup.Items.Add("Delete profile…", null, (_, _) => app.DeleteProfile(p));
        }
        popup.Show(anchor, new Point(0, anchor.Height));
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
        popup.Show(anchor, new Point(0, anchor.Height));
    }
}
