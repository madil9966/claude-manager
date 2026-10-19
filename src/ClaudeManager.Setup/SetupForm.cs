using System.Reflection;

namespace ClaudeManager.Setup;

/// <summary>Wizard: Welcome (with disclaimer) → License → Options → Installing → Finished.</summary>
internal sealed class SetupForm : Form
{
    enum Page { Welcome, License, Options, Installing, Finished }

    const string ShortDisclaimer = "Unofficial community tool. Not affiliated with, endorsed by, or supported by Anthropic.";

    readonly ExistingInstall? existing = Installer.FindExisting();
    readonly Panel content = new() { Dock = DockStyle.Fill };
    readonly Label heading = new() { AutoSize = true };
    readonly Label subheading = new() { AutoSize = true };
    readonly Button back = new() { Text = "< Back", AutoSize = true };
    readonly Button next = new() { Text = "Next >", AutoSize = true };
    readonly Button cancel = new() { Text = "Cancel", AutoSize = true };

    // options
    readonly TextBox dirBox = new();
    readonly CheckBox acceptBox = new() { AutoSize = true };
    readonly CheckBox desktopBox = new() { Text = "Create a Desktop shortcut", AutoSize = true, Checked = true };
    readonly CheckBox startMenuBox = new() { Text = "Create a Start menu shortcut", AutoSize = true, Checked = true };
    readonly CheckBox startupBox = new() { Text = "Start Claude Manager when I sign in to Windows (runs in the tray)", AutoSize = true, Checked = true };
    readonly CheckBox launchBox = new() { Text = "Open Claude Manager now", AutoSize = true, Checked = true };
    readonly Label optionsError = new() { AutoSize = true, ForeColor = Color.Firebrick };

    // progress
    readonly ProgressBar progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
    readonly Label progressText = new() { AutoSize = true };

    Page page = Page.Welcome;
    string? installedExe;
    string? failure;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public SetupForm()
    {
        Text = $"Claude Manager {Installer.Version} Setup";
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(S(620), S(450));
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        dirBox.Text = existing?.Dir ?? Installer.DefaultDir;
        acceptBox.Text = "I accept the license, and I understand this is an unofficial tool that is not endorsed by Anthropic";
        acceptBox.CheckedChanged += (_, _) => UpdateButtons();

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = S(74), BackColor = Color.White };
        var logo = new PictureBox { Size = new Size(S(48), S(48)), Location = new Point(S(16), S(13)), SizeMode = PictureBoxSizeMode.Zoom };
        try { logo.Image = Icon.ExtractIcon(Environment.ProcessPath!, 0, 256)?.ToBitmap(); } catch { }
        heading.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        heading.Location = new Point(S(76), S(14));
        subheading.ForeColor = Color.DimGray;
        subheading.Location = new Point(S(78), S(42));
        header.Controls.AddRange(new Control[] { logo, heading, subheading });
        var headerLine = new Label { Dock = DockStyle.Top, Height = 1, BackColor = Color.Gainsboro };

        // Footer
        var footer = new Panel { Dock = DockStyle.Bottom, Height = S(52) };
        var footerLine = new Label { Dock = DockStyle.Bottom, Height = 1, BackColor = Color.Gainsboro };
        var note = new Label { Text = "Unofficial · not endorsed by Anthropic", AutoSize = true, ForeColor = Color.Gray, Location = new Point(S(14), S(18)) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(0, S(11), S(10), 0) };
        foreach (var b in new[] { back, next, cancel }) b.MinimumSize = new Size(S(84), S(28));
        cancel.Margin = new Padding(S(10), 3, 3, 3);
        buttons.Controls.AddRange(new Control[] { cancel, next, back });
        footer.Controls.Add(buttons);
        footer.Controls.Add(note);

        content.Padding = new Padding(S(24), S(16), S(24), S(8));
        Controls.Add(content);
        Controls.Add(headerLine);
        Controls.Add(header);
        Controls.Add(footerLine);
        Controls.Add(footer);

        back.Click += (_, _) => Go(page - 1);
        next.Click += (_, _) => OnNext();
        cancel.Click += (_, _) => Close();
        FormClosing += (_, e) =>
        {
            if (page == Page.Installing) { e.Cancel = true; return; }
            if (page is Page.Welcome or Page.License or Page.Options && !confirmedCancel && e.CloseReason != CloseReason.WindowsShutDown)
            {
                if (MessageBox.Show(this, "Cancel the installation? Nothing has been changed.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    e.Cancel = true;
                else confirmedCancel = true;
            }
        };
        AcceptButton = next;
        Go(Page.Welcome);
    }

    bool confirmedCancel;

    void OnNext()
    {
        switch (page)
        {
            case Page.Options:
                var err = Installer.CheckDir(dirBox.Text.Trim());
                if (err != null) { optionsError.Text = err; return; }
                Go(Page.Installing);
                RunInstall();
                break;
            case Page.Finished:
                if (installedExe != null && launchBox.Checked)
                    try { Installer.Launch(installedExe); } catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                confirmedCancel = true;
                Close();
                break;
            default:
                Go(page + 1);
                break;
        }
    }

    void Go(Page p)
    {
        page = p;
        content.SuspendLayout();
        foreach (var c in content.Controls.Cast<Control>().ToList())
        {
            content.Controls.Remove(c);
            DetachReused(c); // long-lived inputs keep their state across pages
            c.Dispose();
        }
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, AutoScroll = true };
        content.Controls.Add(col);
        int w = ClientSize.Width - S(56);

        Label Text(string t, bool gray = false, int top = 0) => new()
        {
            Text = t, AutoSize = true, MaximumSize = new Size(w, 0), Margin = new Padding(0, S(top), 0, S(4)),
            ForeColor = gray ? Color.DimGray : SystemColors.ControlText,
        };

        switch (p)
        {
            case Page.Welcome:
                heading.Text = existing != null ? $"Update Claude Manager to {Installer.Version}" : "Welcome to Claude Manager";
                subheading.Text = "Run several Claude accounts side by side, without logging out and in.";
                col.Controls.Add(Text(
                    "Claude Manager is a small tray app that opens the Claude desktop app and Claude Code once per account " +
                    "(for example Work and Personal), each with its own folder.\n\n" +
                    "• Installs for your Windows user only. No administrator rights needed.\n" +
                    "• Your existing Claude setup is never moved, changed or deleted.\n" +
                    "• It never reads, copies or sends your logins, tokens or chats. No telemetry.\n" +
                    "• Uninstall any time from Settings → Apps → Installed apps."));
                if (existing != null)
                    col.Controls.Add(Text($"Version {existing.Version ?? "?"} is installed in {existing.Dir}. It will be updated; your profiles and settings are kept.", top: 6));
                col.Controls.Add(DisclaimerBox(w));
                break;

            case Page.License:
                heading.Text = "License and notice";
                subheading.Text = "Please read before installing.";
                var license = new TextBox
                {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Color.White,
                    Width = w, Height = S(205), Font = new Font("Consolas", 8.5f),
                    Text = (Resource("payload/LICENSE.txt") ?? "MIT License").ReplaceLineEndings("\r\n"),
                };
                col.Controls.Add(license);
                col.Controls.Add(Text(ShortDisclaimer + " \"Claude\" is a trademark of Anthropic, PBC.", gray: true, top: 6));
                acceptBox.MaximumSize = new Size(w, 0);
                acceptBox.Margin = new Padding(0, S(8), 0, 0);
                col.Controls.Add(acceptBox);
                break;

            case Page.Options:
                heading.Text = "Choose options";
                subheading.Text = "The defaults are fine for most people.";
                col.Controls.Add(Text("Install folder:"));
                var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
                dirBox.Width = w - S(100);
                var browse = new Button { Text = "Browse…", AutoSize = true, MinimumSize = new Size(S(84), 0) };
                browse.Click += (_, _) =>
                {
                    using var fb = new FolderBrowserDialog { Description = "Choose where to install Claude Manager", UseDescriptionForTitle = true, ShowNewFolderButton = true };
                    if (fb.ShowDialog(this) == DialogResult.OK)
                    {
                        var chosen = fb.SelectedPath;
                        // Picking a general folder (e.g. Programs) means "inside it".
                        if (!Path.GetFileName(chosen).Contains("Claude", StringComparison.OrdinalIgnoreCase)) chosen = Path.Combine(chosen, "Claude Manager");
                        dirBox.Text = chosen;
                    }
                };
                row.Controls.Add(dirBox);
                row.Controls.Add(browse);
                col.Controls.Add(row);
                col.Controls.Add(Text($"Needs about {Math.Max(1, Installer.PayloadBytes() / (1024 * 1024))} MB. Requires the .NET 9 Desktop Runtime (Windows offers to install it if missing).", gray: true));
                foreach (var cb in new[] { startMenuBox, desktopBox, startupBox })
                {
                    cb.Margin = new Padding(0, S(cb == startMenuBox ? 12 : 4), 0, 0);
                    col.Controls.Add(cb);
                }
                optionsError.MaximumSize = new Size(w, 0);
                optionsError.Margin = new Padding(0, S(10), 0, 0);
                optionsError.Text = "";
                col.Controls.Add(optionsError);
                break;

            case Page.Installing:
                heading.Text = "Installing";
                subheading.Text = "This only takes a moment.";
                progress.Width = w;
                progress.Margin = new Padding(0, S(20), 0, S(8));
                col.Controls.Add(progressText);
                col.Controls.Add(progress);
                break;

            case Page.Finished:
                bool ok = failure == null;
                heading.Text = ok ? "Claude Manager is installed" : "Installation failed";
                subheading.Text = ok ? "You'll find it in the tray (near the clock) and in the Start menu." : "Nothing important was changed.";
                if (ok)
                {
                    col.Controls.Add(Text(
                        "Next steps:\n" +
                        "1. Open Claude Manager and choose your profile names (for example Work and Personal).\n" +
                        "2. Click Desktop on a new profile and sign in with that account.\n" +
                        "3. Click Terminal to use Claude Code with that account (type /login once).\n\n" +
                        "Tip: Settings ▾ in Claude Manager has backup, link routing and \"About\" (with uninstall)."));
                    launchBox.Margin = new Padding(0, S(12), 0, 0);
                    col.Controls.Add(launchBox);
                }
                else
                {
                    col.Controls.Add(Text(failure!));
                    col.Controls.Add(Text("Close any running Claude Manager from its tray icon and run Setup again. " +
                                          "If it keeps failing, report it at github.com/madil9966/claude-manager/issues.", gray: true, top: 8));
                }
                col.Controls.Add(Text(ShortDisclaimer, gray: true, top: 16));
                break;
        }
        content.ResumeLayout();
        UpdateButtons();
    }

    Control[] Reused => new Control[] { dirBox, acceptBox, desktopBox, startMenuBox, startupBox, launchBox, optionsError, progress, progressText };

    /// <summary>Removes the long-lived controls from a page before that page is disposed.</summary>
    void DetachReused(Control parent)
    {
        foreach (var c in parent.Controls.Cast<Control>().ToList())
        {
            if (Reused.Contains(c)) parent.Controls.Remove(c);
            else DetachReused(c);
        }
    }

    void UpdateButtons()
    {
        back.Visible = page is Page.License or Page.Options;
        cancel.Visible = page != Page.Finished;
        cancel.Enabled = page != Page.Installing;
        next.Enabled = page switch
        {
            Page.License => acceptBox.Checked,
            Page.Installing => false,
            _ => true,
        };
        next.Text = page switch
        {
            Page.Options => existing != null ? "Update" : "Install",
            Page.Finished => "Finish",
            _ => "Next >",
        };
    }

    async void RunInstall()
    {
        var o = new InstallOptions(dirBox.Text.Trim(), desktopBox.Checked, startMenuBox.Checked, startupBox.Checked);
        void Report(string s) => BeginInvoke(() => progressText.Text = s);
        try
        {
            installedExe = await Task.Run(() => Program.RunSta(() => Installer.Install(o, Report)));
        }
        catch (Exception ex) { failure = ex.Message; }
        Go(Page.Finished);
    }

    Panel DisclaimerBox(int w)
    {
        var box = new Panel { Width = w, BackColor = Color.FromArgb(255, 248, 225), Padding = new Padding(S(10)), Margin = new Padding(0, S(14), 0, 0), AutoSize = true };
        var l = new Label
        {
            AutoSize = true, MaximumSize = new Size(w - S(20), 0), Location = new Point(S(10), S(8)),
            Text = "Not an Anthropic product\n" +
                   "Claude Manager is a free, unofficial utility made by the community to help people who use more than one Claude account. " +
                   "It is not affiliated with, endorsed by, sponsored by, or supported by Anthropic. \"Claude\" is a trademark of Anthropic, PBC.",
        };
        box.Controls.Add(l);
        box.Paint += (_, e) => e.Graphics.FillRectangle(Brushes.Orange, 0, 0, S(3), box.Height);
        return box;
    }

    static string? Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (s == null) return null;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
