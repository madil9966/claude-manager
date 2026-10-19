using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClaudeManager.Core;
using ClaudeManager.Mac.Core;

namespace ClaudeManager.Mac.UI;

/// <summary>Add / edit a profile: live preview, name, color, icon, folders, inline validation.</summary>
public sealed class ProfileWindow : Window
{
    public static readonly string[] Palette = { "#D97757", "#2F80ED", "#27AE60", "#9B51E0", "#EB5757", "#F2994A", "#00A3A3", "#828282" };
    public static readonly char[] BadNameChars = "\\/:*?\"<>|&%^;!".ToCharArray();

    readonly AppState state;
    readonly Profile? existing;
    readonly bool isDefault;
    readonly MacTheme t;
    readonly TextBox nameBox = new() { Watermark = "For example Work, Personal, Client A", MaxLength = 40 };
    readonly TextBox desktopBox = new();
    readonly TextBox cliBox = new();
    readonly Border previewAvatar = new();
    readonly TextBlock previewName = new() { FontSize = 18, FontWeight = FontWeight.Bold };
    readonly TextBlock previewSub = new() { FontSize = 12 };
    readonly WrapPanel swatches = new();
    readonly WrapPanel icons = new();
    readonly Border error = new() { IsVisible = false, CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Margin = new Thickness(0, 12, 0, 0) };
    readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    bool pathsTouched, settingPaths;

    public bool Saved { get; private set; }
    public string ProfileName => (nameBox.Text ?? "").Trim();
    public string ProfileColor { get; private set; }
    public string? ProfileIcon { get; private set; }
    public string DesktopDir => (desktopBox.Text ?? "").Trim();
    public string CliDir => (cliBox.Text ?? "").Trim();

    public ProfileWindow(AppState state, Profile? existing, MacTheme theme)
    {
        this.state = state;
        this.existing = existing;
        isDefault = existing?.IsDefault == true;
        t = theme;
        ProfileColor = existing?.Color ?? Palette[state.Profiles.Count % Palette.Length];
        ProfileIcon = existing?.Icon;

        Title = existing == null ? "Add profile" : $"Edit profile · {existing.Name}";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = t.Background;

        nameBox.Text = existing?.Name ?? "";
        nameBox.TextChanged += (_, _) =>
        {
            if (existing == null && !pathsTouched) Suggest();
            UpdatePreview();
            BuildIcons();
            error.IsVisible = false;
        };
        desktopBox.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; error.IsVisible = false; };
        cliBox.TextChanged += (_, _) => { if (!settingPaths) pathsTouched = true; error.IsVisible = false; };

        if (isDefault)
        {
            desktopBox.Text = MacPaths.DefaultDesktopDir;
            cliBox.Text = MacPaths.ExistingCliDir;
            desktopBox.IsReadOnly = cliBox.IsReadOnly = true;
        }
        else if (existing != null)
        {
            desktopBox.Text = existing.DesktopDataDir ?? "";
            cliBox.Text = existing.CliConfigDir ?? "";
            pathsTouched = true;
        }
        else Suggest();

        Content = Build();
        UpdatePreview();
        BuildSwatches();
        BuildIcons();
        Opened += (_, _) => nameBox.Focus();
    }

    Control Build()
    {
        Control Label(string text, double top = 18) => new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Foreground = t.Text, Margin = new Thickness(0, top, 0, 6) };
        Control Caption(string text) => new TextBlock { Text = text, FontSize = 12, Foreground = t.TextSecondary, Margin = new Thickness(0, 6, 0, 4) };

        // Preview card
        var preview = new Border
        {
            Background = t.Card, BorderBrush = t.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(16),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 14,
                Children = { previewAvatar, new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 4, Children = { previewName, previewSub } } },
            },
        };
        previewSub.Foreground = t.TextSecondary;

        var desktopRow = FolderRow(desktopBox, "Choose a folder for this profile's desktop app data");
        var cliRow = FolderRow(cliBox, "Choose a folder for this profile's Claude Code settings");
        var note = new TextBlock
        {
            FontSize = 12, Foreground = t.TextTertiary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
            Text = isDefault ? "This is your existing Claude setup. Its folders can't be changed, and Claude Manager never writes to them."
                : existing != null ? "Changing a folder starts that app fresh (a new sign-in). The old folder stays on disk."
                : "Each folder holds this account's own login, history and settings. Folders that already exist are reused as they are.",
        };
        error.Background = t.DangerBack;
        errorText.Foreground = t.Danger;
        error.Child = errorText;

        var cancel = new Button { Content = "Cancel", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var ok = new Button { Content = existing == null ? "Create profile" : "Save", MinWidth = 120, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            var e = Validate();
            if (e != null) { errorText.Text = e; error.IsVisible = true; return; }
            Saved = true;
            Close();
        };

        var body = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 16),
            Children =
            {
                preview,
                Label("Name", 20), nameBox,
                Label("Color"), swatches,
                Label("Icon"), icons,
                new Border { Height = 1, Background = t.Divider, Margin = new Thickness(0, 18, 0, 0) },
                Label("Folders", 14),
                Caption("Desktop app data"), desktopRow,
                Caption("Claude Code"), cliRow,
                note, error,
            },
        };
        var footer = new Border
        {
            Background = t.Dark ? new SolidColorBrush(Color.Parse("#171717")) : new SolidColorBrush(Color.Parse("#EBEBED")),
            BorderBrush = t.Divider, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(22, 14),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
        };
        return new Border { Background = t.Background, Child = new StackPanel { Children = { body, footer } } };
    }

    Control FolderRow(TextBox box, string pickerTitle)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(box);
        if (isDefault) return grid;
        var browse = new Button { Content = "Browse…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = pickerTitle, AllowMultiple = false });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path) box.Text = path;
        };
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    void UpdatePreview()
    {
        var name = ProfileName.Length > 0 ? ProfileName : existing?.Name ?? "New profile";
        previewAvatar.Child = Avatar.Create(ProfileIcon, ProfileColor, name, 48);
        previewName.Text = name;
        previewName.Foreground = ProfileName.Length > 0 ? t.Text : t.TextTertiary;
        previewSub.Text = isDefault ? "Your existing setup · opens Claude exactly as today" : $"Claude ({name})  ·  Claude Code ({name})";
    }

    void BuildSwatches()
    {
        swatches.Children.Clear();
        var colors = Palette.ToList();
        if (!colors.Contains(ProfileColor, StringComparer.OrdinalIgnoreCase)) colors.Add(ProfileColor);
        foreach (var hex in colors)
        {
            bool selected = hex.Equals(ProfileColor, StringComparison.OrdinalIgnoreCase);
            var b = new Button
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse(hex)), BorderBrush = selected ? t.Text : Brushes.Transparent, BorderThickness = new Thickness(selected ? 2.5 : 0),
                Content = selected ? new TextBlock { Text = "✓", Foreground = Brushes.White, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
            };
            ToolTip.SetTip(b, hex);
            var chosen = hex;
            b.Click += (_, _) => { ProfileColor = chosen; BuildSwatches(); BuildIcons(); UpdatePreview(); };
            swatches.Children.Add(b);
        }
    }

    void BuildIcons()
    {
        icons.Children.Clear();
        var specs = new List<string?> { null };                      // initial letter (default)
        specs.AddRange(Avatar.Emojis.Select(e => (string?)(Avatar.EmojiPrefix + e)));
        foreach (var spec in specs)
        {
            bool selected = spec == ProfileIcon;
            var b = new Button
            {
                Width = 44, Height = 44, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(8),
                Background = selected ? new SolidColorBrush(Color.FromArgb(40, 47, 128, 237)) : t.Card,
                BorderBrush = selected ? new SolidColorBrush(Color.Parse("#2F80ED")) : t.CardBorder, BorderThickness = new Thickness(selected ? 2 : 1),
                Content = Avatar.Create(spec, ProfileColor, ProfileName.Length > 0 ? ProfileName : "?", 28),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, spec == null ? "Initial letter (default)" : spec[Avatar.EmojiPrefix.Length..]);
            var chosen = spec;
            b.Click += (_, _) => { ProfileIcon = chosen; BuildIcons(); UpdatePreview(); };
            icons.Children.Add(b);
        }
    }

    void Suggest()
    {
        var (d, c) = MacClaude.SuggestFolders(state, ProfileName.Length == 0 ? "profile" : ProfileName, existing);
        settingPaths = true;
        desktopBox.Text = d;
        cliBox.Text = c;
        settingPaths = false;
    }

    string? Validate()
    {
        var n = ProfileName;
        if (n.Length == 0) return "Enter a name.";
        if (n.IndexOfAny(BadNameChars) >= 0) return "The name can't contain \\ / : * ? \" < > | & % ^ ; !";
        if (state.Profiles.Any(p => !ReferenceEquals(p, existing) && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
            return $"A profile named \"{n}\" already exists.";
        if (isDefault) return null;
        if (DesktopDir.Length == 0 || CliDir.Length == 0) return "Both folders are required.";
        if (MacSafety.CheckProfileFolder(DesktopDir) is { } e1) return $"Desktop data folder {e1}";
        if (MacSafety.CheckProfileFolder(CliDir) is { } e2) return $"Claude Code folder {e2}";
        if (MacSafety.IsSameOrInside(DesktopDir, CliDir) || MacSafety.IsSameOrInside(CliDir, DesktopDir))
            return "Use separate folders for the desktop app and Claude Code.";
        if (MacSafety.OverlappingProfile(state, DesktopDir, existing) is { } o1) return $"The desktop data folder overlaps a folder of profile \"{o1.Name}\".";
        if (MacSafety.OverlappingProfile(state, CliDir, existing) is { } o2) return $"The Claude Code folder overlaps a folder of profile \"{o2.Name}\".";
        return null;
    }
}
