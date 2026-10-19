using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ClaudeManager.Core;
using ClaudeManager.Mac.Core;
using ClaudeManager.Mac.UI;

namespace ClaudeManager.Mac;

/// <summary>Owns the menu bar icon, the main window and every user action (macOS).</summary>
public sealed class AppController
{
    readonly IClassicDesktopStyleApplicationLifetime lifetime;
    readonly MainWindow main;
    readonly TrayIcon tray;
    readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(3) };

    public AppState State { get; }
    public MacDesktop? Desktop { get; private set; }
    public MacCli? Cli { get; private set; }
    public IReadOnlyDictionary<string, long> Usage { get; private set; } = new Dictionary<string, long>();
    public MacTheme Theme { get; private set; }
    public bool Quitting { get; private set; }

    public AppController(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        this.lifetime = lifetime;
        Theme = MacTheme.For(Application.Current!.ActualThemeVariant);
        State = MacDemo.On ? MacDemo.State() : MacStore.Load();
        if (!State.FirstRunDone && State.Profiles.Count == 0)
        {
            // First start: show the existing setup as the Default profile; the user adds accounts from there.
            State.Profiles.Add(new Profile { Name = "Default", Color = "#828282", IsDefault = true, DesktopSignedIn = true });
            State.FirstRunDone = true;
            Save();
        }

        main = new MainWindow(this);
        tray = new TrayIcon { ToolTipText = "Claude Manager" };
        try { tray.Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ClaudeManager/Assets/tray-mac.png"))); } catch { }
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
        RebuildTrayMenu();

        Application.Current!.ActualThemeVariantChanged += (_, _) =>
        {
            Theme = MacTheme.For(Application.Current.ActualThemeVariant);
            main.Rebuild();
        };

        if (MacDemo.On) { Desktop = MacDemo.Desktop; Cli = MacDemo.Cli; Usage = MacDemo.Usage(); main.Rebuild(); }
        else
        {
            StartDetection();
            monitor.Tick += async (_, _) => await RefreshUsage();
            monitor.Start();
        }
        MacLog.Info($"Claude Manager for macOS {typeof(AppController).Assembly.GetName().Version} started (demo={MacDemo.On})");
        if (MacStore.LoadNote is { } note) Dispatcher.UIThread.Post(async () => await Message("Profiles recovered", note));
    }

    void Save()
    {
        try { MacStore.Save(State); }
        catch (Exception ex) { MacLog.Error($"Save failed: {ex.Message}"); }
        RebuildTrayMenu();
    }

    Task<string?> Message(string title, string text, params string[] buttons) => MessageWindow.Ask(main.IsVisible ? main : null, title, text, Theme, buttons);

    public void ShowMain()
    {
        main.Show();
        main.Activate();
    }

    public void Quit()
    {
        Quitting = true;
        monitor.Stop();
        tray.IsVisible = false;
        lifetime.Shutdown();
    }

    public void StartDetection()
    {
        if (MacDemo.On) return;
        Desktop = null; Cli = null;
        main.Rebuild();
        Task.Run(() =>
        {
            var d = MacClaude.FindDesktop();
            var c = MacClaude.FindCli();
            MacLog.Info($"Detected desktop={d.AppPath} {d.Version}; cli={c.Path} {c.Version}");
            Dispatcher.UIThread.Post(() => { Desktop = d; Cli = c; main.Rebuild(); });
        });
    }

    async Task RefreshUsage()
    {
        var instances = await Task.Run(MacClaude.FindInstances);
        var map = new Dictionary<string, long>();
        foreach (var p in State.Profiles)
            if (MacClaude.InstanceFor(p, instances) is { } i) map[p.Id] = i.Bytes;
        if (map.Count == Usage.Count && map.All(kv => Usage.TryGetValue(kv.Key, out var v) && Math.Abs(v - kv.Value) < (8L << 20))) return;
        Usage = map;
        main.UpdateUsage(map);
    }

    void RebuildTrayMenu()
    {
        var menu = new NativeMenu();
        foreach (var p in State.Profiles)
        {
            var sub = new NativeMenu();
            var profile = p;
            var desk = new NativeMenuItem("Open desktop app"); desk.Click += async (_, _) => await LaunchDesktop(profile); sub.Add(desk);
            var term = new NativeMenuItem("Open terminal (last folder)"); term.Click += async (_, _) => await LaunchTerminal(profile, null); sub.Add(term);
            menu.Add(new NativeMenuItem(p.DesktopTitle) { Menu = sub });
        }
        if (State.Profiles.Count > 0) menu.Add(new NativeMenuItemSeparator());
        var open = new NativeMenuItem("Open Claude Manager"); open.Click += (_, _) => ShowMain(); menu.Add(open);
        var about = new NativeMenuItem("About (unofficial, not affiliated with Anthropic)");
        about.Click += async (_, _) => await Message("Claude Manager (unofficial)",
            "A free community utility for running several Claude accounts side by side. It is not affiliated with, endorsed by, or supported by Anthropic. " +
            "\"Claude\" is a trademark of Anthropic, PBC.\n\nIt only chooses which folder each Claude window uses; it never reads, copies or sends your logins, tokens or chats.\n\nMIT License · github.com/madil9966/claude-manager");
        menu.Add(about);
        var quit = new NativeMenuItem("Quit Claude Manager"); quit.Click += (_, _) => Quit(); menu.Add(quit);
        tray.Menu = menu;
    }

    // ---------- desktop ----------

    public async Task LaunchDesktop(Profile p)
    {
        if (MacDemo.On) { await Message("Demo mode", "This is a preview with sample profiles; nothing is launched."); return; }
        var d = Desktop ?? MacClaude.FindDesktop();
        if (!d.Found)
        {
            if (await Message("Claude desktop is not installed", "Install the Claude desktop app into Applications, then click ↻ in Claude Manager.", "Open download page", "Cancel") == "Open download page")
                MacClaude.OpenUrl(MacClaude.DownloadUrl);
            return;
        }
        var running = MacClaude.InstanceFor(p, await Task.Run(MacClaude.FindInstances));
        bool firstSignIn = !p.IsDefault && !p.DesktopSignedIn && running == null;
        if (firstSignIn)
        {
            var others = (await Task.Run(MacClaude.FindInstances)).Count;
            var text = $"Sign in with the account you use for {p.Name}.\n\nOn macOS, sign-in links (including Google sign-in) go to the Claude window that is already open, " +
                       "so quit every other Claude window first (Claude menu → Quit), then sign in." +
                       (others > 0 ? $"\n\n{others} Claude window{(others == 1 ? " is" : "s are")} running right now." : "");
            if (await Message($"First sign-in for {p.DesktopTitle}", text, "Open for sign-in", "Cancel") != "Open for sign-in") return;
        }
        try { MacClaude.EnsureFolders(p); Save(); }
        catch (Exception ex) { await Message($"Could not prepare {p.DesktopTitle}", ex.Message); return; }

        var started = DateTime.UtcNow;
        var r = await Task.Run(() => MacClaude.LaunchDesktop(d, p));
        MacLog.Info($"Launch desktop '{p.Name}' ok={r.Ok} {r.Detail}");
        if (!r.Ok) { await Message($"Could not open {p.DesktopTitle}", r.Message); return; }

        if (firstSignIn)
        {
            // Check that Claude really uses the profile folder before the user signs in.
            await Task.Delay(TimeSpan.FromSeconds(12));
            bool used = MacClaude.FolderTouchedSince(p.DesktopDataDir, started);
            var answer = await Message(used ? $"Sign in to {p.Name}" : "Claude didn't use the profile folder",
                used ? $"✓ Claude is using the {p.Name} folder. Sign in there with the {p.Name} account, then click I'm signed in."
                     : $"Claude has not written to {MacPaths.Shorten(p.DesktopDataDir!)}. If the new window shows an account you're already signed in to, it ignored the profile folder: don't sign in there.",
                "I'm signed in", "Not now");
            if (answer == "I'm signed in") SetSignedIn(p, true);
        }
    }

    public void SetSignedIn(Profile p, bool on) { p.DesktopSignedIn = on; Save(); main.Rebuild(); }

    // ---------- terminal ----------

    public async Task PickFolderAndLaunchTerminal(Profile p)
    {
        ShowMain();
        var picked = await main.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = $"Project folder for {p.TerminalTitle}", AllowMultiple = false });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } dir) await LaunchTerminal(p, dir);
    }

    public async Task LaunchTerminal(Profile p, string? dir)
    {
        if (MacDemo.On) { await Message("Demo mode", "This is a preview with sample profiles; nothing is launched."); return; }
        dir ??= p.RecentDirs.FirstOrDefault(Directory.Exists) ?? MacPaths.Home;
        var cli = Cli ?? await Task.Run(MacClaude.FindCli);
        if (!cli.Found)
        {
            if (await Message("Claude Code is not installed", "Claude Code was not found in your shell's PATH. Install it, then click ↻.", "Open install page", "Cancel") == "Open install page")
                MacClaude.OpenUrl(MacClaude.CliInstallUrl);
            return;
        }
        if (!p.IsDefault && (cli.TooOld || cli.Unverified))
        {
            await Message("Claude Code needs an update",
                (cli.TooOld ? $"Claude Code {cli.Version} is older than {MacCli.MinVersion}." : "Claude Code's version could not be checked.") +
                " Older versions change ~/.claude.json even inside a profile, so profile terminals stay blocked. Run \"claude update\" in Terminal, then click ↻.");
            return;
        }
        try
        {
            MacClaude.EnsureFolders(p);
            var detail = await Task.Run(() => MacClaude.OpenTerminal(p, dir));
            MacLog.Info($"Launch terminal '{p.Name}' {detail}");
            p.AddRecentDir(dir);
            Save();
            main.Rebuild();
        }
        catch (Exception ex) { await Message($"Could not open {p.TerminalTitle}", ex.Message); }
    }

    // ---------- profiles ----------

    public async Task AddProfile()
    {
        ShowMain();
        var w = new ProfileWindow(State, null, Theme);
        await w.ShowDialog(main);
        if (!w.Saved) return;
        var p = new Profile
        {
            Name = w.ProfileName, Color = w.ProfileColor, Icon = w.ProfileIcon,
            DesktopDataDir = MacPaths.Normalize(w.DesktopDir), CliConfigDir = MacPaths.Normalize(w.CliDir),
        };
        try { MacClaude.EnsureFolders(p); }
        catch (Exception ex) { await Message("Could not create the profile", ex.Message); return; }
        State.Profiles.Add(p);
        Save();
        main.Rebuild();
        await Message($"Profile \"{p.Name}\" created", "Click Desktop to do its first sign-in, or Terminal to start Claude Code and type /login.");
    }

    public async Task EditProfile(Profile p)
    {
        ShowMain();
        var w = new ProfileWindow(State, p, Theme);
        await w.ShowDialog(main);
        if (!w.Saved) return;
        p.Name = w.ProfileName;
        p.Color = w.ProfileColor;
        p.Icon = w.ProfileIcon;
        if (!p.IsDefault)
        {
            var desk = MacPaths.Normalize(w.DesktopDir);
            if (!desk.Equals(p.DesktopDataDir, StringComparison.OrdinalIgnoreCase)) { p.DesktopDataDir = desk; p.DesktopSignedIn = false; }
            p.CliConfigDir = MacPaths.Normalize(w.CliDir);
            try { MacClaude.EnsureFolders(p); } catch (Exception ex) { await Message("Could not create the folder", ex.Message); }
        }
        Save();
        main.Rebuild();
    }

    public async Task DeleteProfile(Profile p)
    {
        if (p.IsDefault) return;
        var deletable = new[] { p.DesktopDataDir, p.CliConfigDir }.Where(d => MacClaude.CanDeleteData(State, p, d)).ToList();
        var folders = string.Join("\n", new[] { p.DesktopDataDir, p.CliConfigDir }.Where(d => d != null).Select(d => "• " + MacPaths.Shorten(d!)));
        var choice = await Message($"Delete profile \"{p.Name}\"?",
            $"{folders}\n\nYour existing Claude setup is not affected either way.",
            deletable.Count > 0 ? new[] { "Delete profile, keep its data", "Delete profile and its data", "Cancel" } : new[] { "Delete profile, keep its data", "Cancel" });
        if (choice is null or "Cancel") return;
        if (choice == "Delete profile and its data")
        {
            if (MacClaude.InstanceFor(p, await Task.Run(MacClaude.FindInstances)) != null)
            {
                await Message($"{p.DesktopTitle} is running", "Quit that Claude window first (Claude menu → Quit), then delete again. Nothing was deleted.");
                return;
            }
            var notes = await Task.Run(() => MacClaude.DeleteData(State, p));
            if (notes.Count > 0) await Message($"Profile \"{p.Name}\" deleted", string.Join("\n", notes));
        }
        State.Profiles.Remove(p);
        MacLog.Info($"Deleted profile '{p.Name}' (data {(choice == "Delete profile and its data" ? "removed" : "kept")})");
        Save();
        main.Rebuild();
    }

    // ---------- screenshots (demo mode) ----------

    /// <summary>--screenshot DIR: renders the main window and the edit dialog in light and dark mode to PNG (2x).</summary>
    public async Task RenderScreenshots(string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            var name = variant == ThemeVariant.Dark ? "dark" : "light";
            Application.Current!.RequestedThemeVariant = variant;
            Theme = MacTheme.For(variant);
            main.Rebuild();
            main.Show();
            await Task.Delay(900);
            SavePng(main, Path.Combine(dir, $"macos-main-{name}.png"));

            var edit = new ProfileWindow(State, State.Profiles.First(p => p.Name == "Work"), Theme);
            edit.Show(main);
            await Task.Delay(900);
            SavePng(edit, Path.Combine(dir, $"macos-edit-profile-{name}.png"));
            edit.Close();
        }
        Console.WriteLine($"Screenshots written to {dir}");
    }

    static void SavePng(Window w, string path)
    {
        if (w.Content is not Control c) return;
        const double scale = 2;
        var size = c.Bounds.Size;
        using var rtb = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), new Vector(96 * scale, 96 * scale));
        rtb.Render(c);
        rtb.Save(path);
        Console.WriteLine($"  {Path.GetFileName(path)} {rtb.PixelSize.Width}x{rtb.PixelSize.Height}");
    }
}
