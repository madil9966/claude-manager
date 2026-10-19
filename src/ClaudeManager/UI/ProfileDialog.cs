using ClaudeManager.Core;

namespace ClaudeManager.UI;

internal sealed class ProfileDialog : Form
{
    public static readonly string[] Palette = { "#D97757", "#2F80ED", "#27AE60", "#9B51E0", "#EB5757", "#F2994A", "#00A3A3", "#828282" };
    public static readonly char[] BadNameChars = "\\/:*?\"<>|&%^;!".ToCharArray();

    readonly AppState state;
    readonly Profile? existing;
    readonly TextBox nameBox = new();
    readonly TextBox desktopBox = new();
    readonly TextBox cliBox = new();
    readonly FlowLayoutPanel swatches = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    readonly FlowLayoutPanel iconChoices = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    readonly ToolTip iconTips = new();
    /// <summary>A custom image imported in this dialog session that isn't the profile's saved one (cleaned up if unused).</summary>
    readonly List<string> importedThisSession = new();
    readonly Label error = new() { AutoSize = true, ForeColor = Color.Firebrick };
    bool pathsTouched, settingPaths;

    public string ProfileName => nameBox.Text.Trim();
    public string ProfileColor { get; private set; }
    public string ProfileIcon { get; private set; }
    public string DesktopDir => desktopBox.Text.Trim();
    public string CliDir => cliBox.Text.Trim();

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public ProfileDialog(AppState state, Profile? existing)
    {
        this.state = state;
        this.existing = existing;
        bool isDefault = existing?.IsDefault == true;
        ProfileColor = existing?.Color ?? Palette[state.Profiles.Count % Palette.Length];
        ProfileIcon = existing?.Icon ?? IconFactory.ClaudeSpec;

        Text = existing == null ? "Add profile" : $"Edit profile: {existing.Name}";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(S(12));

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Location = new Point(S(12), S(12)) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(360)));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        nameBox.Dock = desktopBox.Dock = cliBox.Dock = DockStyle.Fill;
        nameBox.Text = existing?.Name ?? "";
        nameBox.TextChanged += (_, _) =>
        {
            if (existing == null && !pathsTouched) Suggest();
            BuildIconChoices(); // "Initial letter" preview follows the name
        };
        desktopBox.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; };
        cliBox.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; };

        BuildSwatches();
        AddRow(grid, "Name", nameBox, null);
        AddRow(grid, "Color", swatches, null);
        iconChoices.MaximumSize = new Size(S(360), 0);
        AddRow(grid, "Icon", iconChoices, ChooseImageButton());

        if (isDefault)
        {
            desktopBox.Text = Paths.DefaultDesktopDir;
            cliBox.Text = Paths.ExistingCliDir;
            desktopBox.ReadOnly = cliBox.ReadOnly = true;
            AddRow(grid, "Desktop data", desktopBox, null);
            AddRow(grid, "Claude Code", cliBox, null);
            AddNote(grid, "This is your existing Claude setup. Its folders can't be changed and Claude Manager never writes to them.");
        }
        else
        {
            AddRow(grid, "Desktop data folder", desktopBox, BrowseButton(desktopBox));
            AddRow(grid, "Claude Code folder", cliBox, BrowseButton(cliBox));
            if (existing != null)
            {
                desktopBox.Text = existing.DesktopDataDir ?? "";
                cliBox.Text = existing.CliConfigDir ?? "";
                pathsTouched = true;
                AddNote(grid, "Changing a folder starts that app fresh (new sign-in). The old folder is left on disk.");
            }
            else
            {
                Suggest();
                AddNote(grid, "Each folder holds this account's own login, history and settings. Existing folders are reused as they are.");
            }
        }

        grid.Controls.Add(error, 0, grid.RowCount);
        grid.SetColumnSpan(error, 3);
        grid.RowCount++;

        var ok = new Button { Text = existing == null ? "Create" : "Save", AutoSize = true, MinimumSize = new Size(S(84), 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(S(84), 0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            var e = Validate();
            if (e != null) { error.Text = e; return; }
            DialogResult = DialogResult.OK;
        };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Anchor = AnchorStyles.Right, Margin = new Padding(0, S(8), 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        grid.Controls.Add(buttons, 0, grid.RowCount);
        grid.SetColumnSpan(buttons, 3);
        grid.RowCount++;

        Controls.Add(grid);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    void Suggest()
    {
        var name = ProfileName.Length == 0 ? "profile" : ProfileName;
        var (d, c) = ProfileService.SuggestFolders(state, name, state.Layout, existing);
        settingPaths = true;
        desktopBox.Text = d;
        cliBox.Text = c;
        settingPaths = false;
    }

    void BuildSwatches()
    {
        swatches.Controls.Clear();
        foreach (var hex in Palette.Concat(Palette.Contains(ProfileColor) ? Array.Empty<string>() : new[] { ProfileColor }))
        {
            var b = new Button
            {
                Size = new Size(S(26), S(26)),
                FlatStyle = FlatStyle.Flat,
                BackColor = IconFactory.Parse(hex),
                Margin = new Padding(0, 0, S(4), 0),
            };
            bool selected = hex.Equals(ProfileColor, StringComparison.OrdinalIgnoreCase);
            b.FlatAppearance.BorderSize = selected ? 3 : 1;
            b.FlatAppearance.BorderColor = selected ? Color.Black : Color.Silver;
            b.Click += (_, _) => { ProfileColor = hex; BuildSwatches(); };
            swatches.Controls.Add(b);
        }
        var custom = new Button { Text = "Custom…", AutoSize = true };
        custom.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = IconFactory.Parse(ProfileColor), FullOpen = true };
            if (cd.ShowDialog(this) == DialogResult.OK) { ProfileColor = IconFactory.ToHex(cd.Color); BuildSwatches(); }
        };
        swatches.Controls.Add(custom);
        BuildIconChoices(); // presets and the Claude badge use the profile color
    }

    /// <summary>Claude icon (default), the presets, and the custom image if one is chosen.</summary>
    void BuildIconChoices()
    {
        iconChoices.SuspendLayout();
        iconTips.RemoveAll();
        foreach (var c in iconChoices.Controls.Cast<Control>().ToList()) c.Dispose(); // images are cached/shared, not owned
        var specs = new List<string> { IconFactory.ClaudeSpec };
        specs.AddRange(IconFactory.Presets.Select(p => IconFactory.PresetPrefix + p.Key));
        if (ProfileIcon.StartsWith(IconFactory.FilePrefix)) specs.Add(ProfileIcon);

        int img = S(22);
        foreach (var spec in specs)
        {
            bool selected = spec == ProfileIcon;
            var b = new Button
            {
                Size = new Size(S(32), S(32)),
                FlatStyle = FlatStyle.Flat,
                Image = IconFactory.Image(spec, ProfileColor, nameBox.Text, img),
                Margin = new Padding(0, 0, S(4), S(4)),
                AccessibleName = IconFactory.Describe(spec),
            };
            b.FlatAppearance.BorderSize = selected ? 3 : 1;
            b.FlatAppearance.BorderColor = selected ? Color.Black : Color.Silver;
            var chosen = spec;
            b.Click += (_, _) => { ProfileIcon = chosen; BuildIconChoices(); };
            iconTips.SetToolTip(b, IconFactory.Describe(spec) + (spec == IconFactory.ClaudeSpec ? " (default), with a dot in the profile color" : ""));
            iconChoices.Controls.Add(b);
        }
        iconChoices.ResumeLayout();
    }

    Button ChooseImageButton()
    {
        var b = new Button { Text = "Choose image…", AutoSize = true, Anchor = AnchorStyles.Top };
        b.Click += (_, _) =>
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
                BuildIconChoices();
            }
            catch (Exception ex) { error.Text = $"That image could not be used: {ex.Message}"; }
        };
        return b;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Remove imported images that weren't kept (cancelled, or another icon chosen afterwards).
        var kept = DialogResult == DialogResult.OK ? ProfileIcon : existing?.Icon;
        foreach (var spec in importedThisSession.Where(s => s != kept))
            try { File.Delete(spec[IconFactory.FilePrefix.Length..]); } catch { }
        iconTips.Dispose();
        base.OnFormClosed(e);
    }

    Button BrowseButton(TextBox target)
    {
        var b = new Button { Text = "Browse…", AutoSize = true };
        b.Click += (_, _) =>
        {
            using var fb = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Choose a dedicated folder for this profile", ShowNewFolderButton = true };
            try { var parent = Path.GetDirectoryName(Paths.Normalize(target.Text)); if (Directory.Exists(parent)) fb.InitialDirectory = parent; } catch { }
            if (fb.ShowDialog(this) == DialogResult.OK) target.Text = fb.SelectedPath;
        };
        return b;
    }

    static void AddRow(TableLayoutPanel grid, string label, Control field, Control? extra)
    {
        int row = grid.RowCount++;
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 6) }, 0, row);
        grid.Controls.Add(field, 1, row);
        if (extra != null) grid.Controls.Add(extra, 2, row);
    }

    void AddNote(TableLayoutPanel grid, string text)
    {
        int row = grid.RowCount++;
        var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(S(520), 0), Margin = new Padding(0, S(4), 0, S(4)) };
        grid.Controls.Add(l, 0, row);
        grid.SetColumnSpan(l, 3);
    }

    new string? Validate()
    {
        var n = ProfileName;
        if (n.Length == 0) return "Enter a name.";
        if (n.Length > 40) return "Keep the name under 40 characters.";
        if (n.IndexOfAny(BadNameChars) >= 0) return "The name can't contain \\ / : * ? \" < > | & % ^ ; !";
        if (state.Profiles.Any(p => !ReferenceEquals(p, existing) && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return $"A profile named \"{n}\" already exists.";
        if (existing?.IsDefault == true) return null;

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
