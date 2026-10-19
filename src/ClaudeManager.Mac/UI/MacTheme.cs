using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ClaudeManager.Core;

namespace ClaudeManager.Mac.UI;

/// <summary>Colors for light/dark mode (follows macOS appearance), matching the Windows app's look.</summary>
public sealed class MacTheme
{
    public bool Dark { get; init; }
    public IBrush Background { get; init; } = Brushes.White;
    public IBrush Card { get; init; } = Brushes.White;
    public IBrush CardBorder { get; init; } = Brushes.LightGray;
    public IBrush Text { get; init; } = Brushes.Black;
    public IBrush TextSecondary { get; init; } = Brushes.Gray;
    public IBrush TextTertiary { get; init; } = Brushes.Gray;
    public IBrush Success { get; init; } = Brushes.Green;
    public IBrush Warning { get; init; } = Brushes.Orange;
    public IBrush WarningBack { get; init; } = Brushes.LightYellow;
    public IBrush Danger { get; init; } = Brushes.Red;
    public IBrush DangerBack { get; init; } = Brushes.MistyRose;
    public IBrush Divider { get; init; } = Brushes.LightGray;

    public static MacTheme For(ThemeVariant? v)
    {
        bool dark = v == ThemeVariant.Dark;
        var forced = Environment.GetEnvironmentVariable("CLAUDE_MANAGER_THEME");
        if (forced == "dark") dark = true; else if (forced == "light") dark = false;
        static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
        return dark
            ? new MacTheme
            {
                Dark = true, Background = B("#1E1E1E"), Card = B("#2A2A2A"), CardBorder = B("#383838"), Text = B("#F2F2F2"),
                TextSecondary = B("#C2C2C2"), TextTertiary = B("#8E8E8E"), Success = B("#6CCB5F"), Warning = B("#F5C451"),
                WarningBack = B("#433519"), Danger = B("#FF99A4"), DangerBack = B("#442726"), Divider = B("#383838"),
            }
            : new MacTheme
            {
                Dark = false, Background = B("#F4F4F5"), Card = B("#FFFFFF"), CardBorder = B("#E4E4E7"), Text = B("#1A1A1A"),
                TextSecondary = B("#5F5F66"), TextTertiary = B("#8A8A90"), Success = B("#0F7B0F"), Warning = B("#9D5D00"),
                WarningBack = B("#FFF4CE"), Danger = B("#C42B1C"), DangerBack = B("#FDE7E9"), Divider = B("#E4E4E7"),
            };
    }
}

/// <summary>Round profile avatar: an emoji preset or the name's first letter on the profile color.</summary>
public static class Avatar
{
    public const string EmojiPrefix = "emoji:";
    public static readonly string[] Emojis = { "💼", "🏠", "💻", "⭐", "❤️", "⚡", "🎓", "🌐", "🧪", "🎨" };

    public static Control Create(Profile p, double size) => Create(p.Icon, p.Color, p.Name, size);

    public static Control Create(string? icon, string color, string name, double size)
    {
        Color c;
        try { c = Color.Parse(color); } catch { c = Colors.Gray; }
        string text;
        double fontSize;
        if (icon != null && icon.StartsWith(EmojiPrefix)) { text = icon[EmojiPrefix.Length..]; fontSize = size * 0.5; }
        else { var t = name.Trim(); text = t.Length == 0 ? "?" : char.ToUpperInvariant(t[0]).ToString(); fontSize = size * 0.45; }
        bool emoji = icon != null && icon.StartsWith(EmojiPrefix);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = emoji ? new SolidColorBrush(Color.FromArgb(48, c.R, c.G, c.B)) : new SolidColorBrush(c),
            BorderBrush = new SolidColorBrush(c), BorderThickness = new Thickness(emoji ? 2 : 0),
            Child = new TextBlock
            {
                Text = text, FontSize = fontSize, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}

/// <summary>Small message/question dialog in the app's style. Returns the clicked button's text (null if closed).</summary>
public sealed class MessageWindow : Window
{
    string? result;

    public MessageWindow(string title, string message, MacTheme t, params string[] buttons)
    {
        Title = "Claude Manager";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = t.Background;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            var label = buttons[i];
            var b = new Button { Content = label, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) { b.Classes.Add("accent"); b.IsDefault = true; }
            if (label is "Cancel" or "Not now") b.IsCancel = true;
            b.Click += (_, _) => { result = label; Close(); };
            row.Children.Add(b);
        }
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Children =
            {
                new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = t.Text, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = message, Margin = new Thickness(0, 10, 0, 0), Foreground = t.TextSecondary, TextWrapping = TextWrapping.Wrap },
                row,
            },
        };
    }

    public static async Task<string?> Ask(Window? owner, string title, string message, MacTheme t, params string[] buttons)
    {
        var w = new MessageWindow(title, message, t, buttons.Length == 0 ? new[] { "OK" } : buttons);
        if (owner is { IsVisible: true }) await w.ShowDialog(owner);
        else { var tcs = new TaskCompletionSource(); w.Closed += (_, _) => tcs.TrySetResult(); w.Show(); await tcs.Task; }
        return w.result;
    }
}
