using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ClaudeManager.Core;
using ClaudeManager.Mac.Core;

namespace ClaudeManager.Mac.UI;

/// <summary>The main window: status, one card per profile (Desktop / Terminal), add profile.</summary>
public sealed class MainWindow : Window
{
    readonly AppController app;
    readonly Dictionary<string, TextBlock> statusLines = new();
    TextBlock? summary;
    public MacTheme T => app.Theme;

    public MainWindow(AppController app)
    {
        this.app = app;
        Title = "Claude Manager";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ClaudeManager/Assets/app-256.png"))); } catch { }
        // Closing hides it; Claude Manager keeps running in the menu bar.
        Closing += (_, e) => { if (!app.Quitting) { e.Cancel = true; Hide(); } };
        Rebuild();
    }

    public void Rebuild()
    {
        var t = T;
        Background = t.Background;
        statusLines.Clear();
        var root = new StackPanel { Margin = new Thickness(16, 14, 16, 10), Spacing = 0 };

        // Header
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        Image? logo = null;
        try { logo = new Image { Source = new Bitmap(AssetLoader.Open(new Uri("avares://ClaudeManager/Assets/app-256.png"))), Width = 26, Height = 26, Margin = new Thickness(0, 0, 10, 0) }; } catch { }
        if (logo != null) header.Children.Add(logo);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "Claude Manager", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = t.Text, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new Border
        {
            BorderBrush = t.TextTertiary, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Unofficial", FontSize = 11, Foreground = t.TextSecondary },
        });
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var refresh = new Button { Content = "↻", FontSize = 15, Padding = new Thickness(8, 2), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(refresh, "Look for the Claude desktop app and Claude Code again");
        refresh.Click += (_, _) => app.StartDetection();
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        root.Children.Add(header);

        // Status
        root.Children.Add(StatusLine(app.Desktop == null ? ("…", "Looking for the Claude desktop app…", null, null)
            : app.Desktop.Found ? ("✓", $"Claude desktop {app.Desktop.Version}", null, null)
            : ("✕", "Claude desktop app not installed", "Download", () => MacClaude.OpenUrl(MacClaude.DownloadUrl))));
        var cli = app.Cli;
        root.Children.Add(StatusLine(cli == null ? ("…", "Looking for Claude Code…", null, null)
            : !cli.Found ? ("✕", "Claude Code not installed", "Install", () => MacClaude.OpenUrl(MacClaude.CliInstallUrl))
            : cli.TooOld ? ("!", $"Claude Code {cli.Version} is too old: run claude update", null, null)
            : cli.Unverified ? ("!", "Claude Code version couldn't be checked", "Check again", app.StartDetection)
            : ("✓", $"Claude Code {cli.Version}", null, null)));

        // Section header
        var section = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 14, 0, 8) };
        section.Children.Add(new TextBlock { Text = "Profiles", FontWeight = FontWeight.SemiBold, Foreground = t.Text });
        summary = new TextBlock { FontSize = 12, Foreground = t.TextSecondary };
        Grid.SetColumn(summary, 1);
        section.Children.Add(summary);
        root.Children.Add(section);

        // Cards
        var cards = new StackPanel { Spacing = 8 };
        foreach (var p in app.State.Profiles) cards.Children.Add(Card(p));
        root.Children.Add(cards.Children.Count > 4 ? new ScrollViewer { MaxHeight = 4 * 112, Content = cards } : cards);

        var add = new Button
        {
            Content = app.State.Profiles.Count == 0 ? "+  Add your first profile" : "+  Add profile",
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(0, 9),
            Background = Brushes.Transparent, BorderBrush = t.TextTertiary, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
        };
        add.Click += async (_, _) => await app.AddProfile();
        root.Children.Add(add);

        // Footer
        root.Children.Add(new Border { Height = 1, Background = t.Divider, Margin = new Thickness(0, 14, 0, 8) });
        root.Children.Add(new TextBlock
        {
            Text = "Unofficial community tool. Not affiliated with Anthropic.", FontSize = 11, Foreground = t.TextTertiary,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        Content = new Border { Background = t.Background, Child = root }; // solid, so rendered screenshots aren't transparent
        UpdateUsage(app.Usage);
    }

    Control StatusLine((string Glyph, string Text, string? Action, Action? OnAction) s)
    {
        var t = T;
        var color = s.Glyph switch { "✓" => t.Success, "✕" => t.Danger, "!" => t.Warning, _ => t.TextTertiary };
        var tb = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2), TextWrapping = TextWrapping.Wrap };
        tb.Inlines!.Add(new Run(s.Glyph + "  ") { Foreground = color, FontWeight = FontWeight.Bold });
        tb.Inlines.Add(new Run(s.Text) { Foreground = t.TextSecondary });
        if (s.Action == null) return tb;
        var link = new Button
        {
            Content = s.Action, FontSize = 12, Padding = new Thickness(6, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#2F80ED")), Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        link.Click += (_, _) => s.OnAction?.Invoke();
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { tb, link } };
    }

    Control Card(Profile p)
    {
        var t = T;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var avatar = Avatar.Create(p, 40);
        avatar.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(avatar);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = t.Text, TextTrimming = TextTrimming.CharacterEllipsis });
        var status = new TextBlock { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        statusLines[p.Id] = status;
        SetStatus(p, status, null);
        text.Children.Add(status);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var more = new Button { Content = "•••", FontSize = 11, Padding = new Thickness(8, 4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Top };
        more.Flyout = MoreMenu(p);
        ToolTip.SetTip(more, "Edit, folders, delete…");
        Grid.SetColumn(more, 2);
        grid.Children.Add(more);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Margin = new Thickness(0, 12, 0, 0) };
        var desktop = new Button { Content = "🖥  Desktop", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 7) };
        desktop.Classes.Add("accent");
        desktop.Click += async (_, _) => await app.LaunchDesktop(p);
        ToolTip.SetTip(desktop, $"Open {p.DesktopTitle}");
        var terminal = new Button { Content = ">_  Terminal  ▾", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(0, 7) };
        terminal.Flyout = TerminalMenu(p);
        ToolTip.SetTip(terminal, $"Open {p.TerminalTitle} in a project folder");
        buttons.Children.Add(desktop);
        Grid.SetColumn(terminal, 2);
        buttons.Children.Add(terminal);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = t.Card, BorderBrush = t.CardBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12), Child = grid,
        };
    }

    void SetStatus(Profile p, TextBlock tb, long? bytes)
    {
        var t = T;
        if (bytes != null) { tb.Text = $"●  Running · {MacClaude.FormatBytes(bytes.Value)}"; tb.Foreground = t.Success; return; }
        if (!p.IsDefault && !p.DesktopSignedIn) { tb.Text = "First sign-in needed"; tb.Foreground = t.Warning; return; }
        tb.Text = p.IsDefault ? "Your existing setup · unchanged" : MacPaths.Shorten(p.CliConfigDir ?? "");
        tb.Foreground = t.TextSecondary;
    }

    public void UpdateUsage(IReadOnlyDictionary<string, long> usage)
    {
        foreach (var p in app.State.Profiles)
            if (statusLines.TryGetValue(p.Id, out var tb)) SetStatus(p, tb, usage.TryGetValue(p.Id, out var b) ? b : null);
        if (summary != null)
            summary.Text = usage.Count == 0 ? "" : $"● {usage.Count} running · {MacClaude.FormatBytes(usage.Values.Sum())}";
        if (summary != null) summary.Foreground = usage.Count == 0 ? T.TextSecondary : T.Success;
    }

    MenuFlyout TerminalMenu(Profile p)
    {
        var f = new MenuFlyout();
        foreach (var d in p.RecentDirs.Where(d => Directory.Exists(d) || MacDemo.On).Take(8))
        {
            var dir = d;
            var mi = new MenuItem { Header = MacPaths.Shorten(dir) };
            mi.Click += async (_, _) => await app.LaunchTerminal(p, dir);
            f.Items.Add(mi);
        }
        if (f.Items.Count > 0) f.Items.Add(new Separator());
        var home = new MenuItem { Header = $"Home folder ({MacPaths.Shorten(MacPaths.Home)})" };
        home.Click += async (_, _) => await app.LaunchTerminal(p, MacPaths.Home);
        f.Items.Add(home);
        var choose = new MenuItem { Header = "Choose folder…" };
        choose.Click += async (_, _) => await app.PickFolderAndLaunchTerminal(p);
        f.Items.Add(choose);
        return f;
    }

    MenuFlyout MoreMenu(Profile p)
    {
        var f = new MenuFlyout();
        MenuItem Item(string header, Func<Task> action) { var mi = new MenuItem { Header = header }; mi.Click += async (_, _) => await action(); return mi; }
        f.Items.Add(Item("Edit name, icon, color, folders…", () => app.EditProfile(p)));
        f.Items.Add(new Separator());
        f.Items.Add(Item("Show Claude Code folder in Finder", () => { MacClaude.Reveal(p.IsDefault ? MacPaths.ExistingCliDir : p.CliConfigDir ?? ""); return Task.CompletedTask; }));
        f.Items.Add(Item("Show desktop data folder in Finder", () => { MacClaude.Reveal(p.IsDefault ? MacPaths.DefaultDesktopDir : p.DesktopDataDir ?? ""); return Task.CompletedTask; }));
        if (!p.IsDefault)
        {
            f.Items.Add(new Separator());
            f.Items.Add(p.DesktopSignedIn
                ? Item("Redo first desktop sign-in", () => { app.SetSignedIn(p, false); return Task.CompletedTask; })
                : Item("Mark desktop as signed in", () => { app.SetSignedIn(p, true); return Task.CompletedTask; }));
            f.Items.Add(new Separator());
            f.Items.Add(Item("Delete profile…", () => app.DeleteProfile(p)));
        }
        return f;
    }
}
