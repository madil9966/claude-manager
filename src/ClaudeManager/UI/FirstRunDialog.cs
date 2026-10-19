using ClaudeManager.Core;

namespace ClaudeManager.UI;

internal sealed class FirstRunDialog : Form
{
    readonly CheckBox includeDefault = new() { Text = "Show my current Claude setup as a profile named:", AutoSize = true, Checked = true };
    readonly TextBox defaultName = new() { Text = "Default" };
    readonly TextBox name1 = new() { Text = "Work" };
    readonly TextBox name2 = new() { Text = "Personal" };
    readonly RadioButton layoutHome = new() { Text = "Next to your user folder (.claude-work, %APPDATA%\\Claude-Work)", AutoSize = true, Checked = true };
    readonly RadioButton layoutParent = new() { Text = "All together in %USERPROFILE%\\.claude-accounts", AutoSize = true };
    readonly CheckBox backup = new() { Text = "Back up my current Claude settings first (recommended)", AutoSize = true, Checked = true };
    readonly Label error = new() { AutoSize = true, ForeColor = Color.Firebrick };

    public bool IncludeDefault => includeDefault.Checked;
    public string DefaultName => defaultName.Text.Trim();
    public FolderLayout FolderLayout => layoutParent.Checked ? FolderLayout.AccountsParent : FolderLayout.UserProfile;
    public bool BackupFirst => backup.Checked;
    public List<(string Name, string Color)> NewProfiles { get; } = new();

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public FirstRunDialog()
    {
        Text = "Welcome to Claude Manager";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(14));
        ShowInTaskbar = true;
        TopMost = true;

        var col = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Location = new Point(S(14), S(14)) };
        col.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        col.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(200)));
        int w = S(560);

        void Span(Control c, int top = 4)
        {
            c.Margin = new Padding(0, S(top), 0, S(2));
            col.Controls.Add(c, 0, col.RowCount);
            col.SetColumnSpan(c, 2);
            col.RowCount++;
        }
        void Pair(Control a, Control b)
        {
            a.Anchor = AnchorStyles.Left;
            b.Dock = DockStyle.Fill;
            col.Controls.Add(a, 0, col.RowCount);
            col.Controls.Add(b, 1, col.RowCount);
            col.RowCount++;
        }

        Span(new Label
        {
            AutoSize = true, MaximumSize = new Size(w, 0),
            Text = "Claude Manager runs each Claude account from its own folder, so you can use several at once.\n\n" +
                   $"Your current setup is never moved, changed or deleted:\n   {Paths.Shorten(Paths.ExistingCliDir)}   {Paths.Shorten(Paths.HomeClaudeJson)}\n   {Paths.DefaultDesktopDir}",
        }, 0);
        Pair(includeDefault, defaultName);
        Span(new Label { Text = "It opens exactly like today, with your current account and settings.", AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(w, 0) }, 0);

        Span(new Label { Text = "New profiles (leave a box empty to skip it):", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 12);
        Pair(new Label { Text = "Profile 1", AutoSize = true }, name1);
        Pair(new Label { Text = "Profile 2", AutoSize = true }, name2);

        Span(new Label { Text = "Where to keep new profile folders:", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, 12);
        Span(layoutHome, 0);
        Span(layoutParent, 0);

        Span(backup, 12);
        Span(new Label
        {
            AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = SystemColors.GrayText,
            Text = $"Copies settings, CLAUDE.md, agents, commands, skills, history and MCP config into a zip in {Paths.Shorten(Paths.BackupDir)}. Login tokens are never copied.",
        }, 0);
        Span(new Label
        {
            AutoSize = true, MaximumSize = new Size(w, 0), ForeColor = SystemColors.GrayText,
            Text = About.ShortDisclaimer + " A free community utility; \"Claude\" is a trademark of Anthropic.",
        }, 12);
        Span(error, 6);

        var ok = new Button { Text = "Create profiles", AutoSize = true, Anchor = AnchorStyles.Right };
        ok.Click += (_, _) =>
        {
            var e = Collect();
            if (e != null) { error.Text = e; return; }
            DialogResult = DialogResult.OK;
        };
        ok.Margin = new Padding(0, S(8), 0, 0);
        col.Controls.Add(ok, 1, col.RowCount);
        col.RowCount++;
        includeDefault.CheckedChanged += (_, _) => defaultName.Enabled = includeDefault.Checked;

        Controls.Add(col);
        AcceptButton = ok;
    }

    string? Collect()
    {
        NewProfiles.Clear();
        var names = new List<string>();
        if (IncludeDefault) names.Add(DefaultName);
        foreach (var n in new[] { name1.Text.Trim(), name2.Text.Trim() }.Where(n => n.Length > 0))
            names.Add(n);
        foreach (var n in names)
        {
            if (n.Length == 0) return "Enter a name for your current setup, or untick that box.";
            if (n.Length > 40) return $"\"{n}\" is too long (40 characters max).";
            if (n.IndexOfAny(ProfileDialog.BadNameChars) >= 0) return $"\"{n}\" contains a character that isn't allowed (\\ / : * ? \" < > | & % ^ ; !).";
        }
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) return "Each profile needs a different name.";
        int i = 0;
        foreach (var n in new[] { name1.Text.Trim(), name2.Text.Trim() }.Where(n => n.Length > 0))
            NewProfiles.Add((n, ProfileDialog.Palette[i++]));
        return null;
    }
}
