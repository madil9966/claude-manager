using System.Text;
using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>Owns the tray icon, the panel and every user action.</summary>
internal sealed class TrayApp : ApplicationContext
{
    public AppState State { get; }
    public DesktopInstall? Desktop { get; private set; }
    public CliInstall? Cli { get; private set; }

    readonly NotifyIcon tray;
    readonly MainPanel panel;
    readonly CancellationTokenSource cts = new();
    /// <summary>
    /// Profile waiting for its first desktop sign-in. Stored in profiles.json so a restart mid-sign-in
    /// doesn't send the sign-in link elsewhere; expires after <see cref="AppState.PendingSignInLifetime"/>.
    /// Callers Save() after setting it.
    /// </summary>
    string? pendingSignInId
    {
        get => State.PendingSignInId != null && State.PendingSignInUtc is { } t && DateTime.UtcNow - t < AppState.PendingSignInLifetime
            ? State.PendingSignInId : null;
        set { State.PendingSignInId = value; State.PendingSignInUtc = value == null ? null : DateTime.UtcNow; }
    }
    SignInWaitForm? signInForm;

    IWin32Window? DialogOwner => panel.Visible ? panel : null;

    public TrayApp(string[] args)
    {
        State = Demo.On ? Demo.State() : ProfileStore.Load();
        var icon = IconFactory.AppIcon();
        panel = new MainPanel(this) { Icon = icon };
        _ = panel.Handle; // needed for BeginInvoke from background threads

        tray = new NotifyIcon { Icon = icon, Text = "Claude Manager", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        tray.ContextMenuStrip.Opening += (_, e) => { BuildTrayMenu(); e.Cancel = false; };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) TogglePanel(); };

        Ipc.StartServer(a => panel.BeginInvoke(() => HandleCommand(a)), cts.Token);
        StartDetection();

        if (Demo.On)
        {
            // Screenshots only: sample data, no routing, monitoring, repair or saving.
            Log.Info("Demo mode: sample profiles, nothing is saved");
            profileSnapshot = State.Profiles.ToArray();
            panel.BeginInvoke(() => { ApplyUsage(Demo.Usage()); HandleCommand(args, startup: true); });
            return;
        }

        // Claude re-registers claude:// for itself on every start, so keep re-applying the routing.
        EnsureLinkRouting();
        ProtocolHandler.Watch(EnsureLinkRouting, cts.Token);

        profileSnapshot = State.Profiles.ToArray();
        _ = Task.Run(() => MonitorLoop(cts.Token));

        // Portable copy moved since last run? Repoint the startup entry and shortcuts (off the UI thread: COM/registry).
        var repair = new Thread(() => { try { Install.RepairMovedExe(); } catch (Exception ex) { Log.Error($"Self-repair: {ex.Message}"); } })
            { IsBackground = true, Name = "self-repair" };
        repair.SetApartmentState(ApartmentState.STA); // WScript.Shell shortcuts
        repair.Start();
        panel.BeginInvoke(ResumePendingSignIn);

        if (ProfileStore.LoadNote is { } note)
            panel.BeginInvoke(() => { Save(); Dialogs.Info(null, "Profiles recovered", note); });

        if (!State.FirstRunDone) panel.BeginInvoke(() => { RunFirstRun(); HandleCommand(args, startup: true); });
        else panel.BeginInvoke(() => HandleCommand(args, startup: true));
    }

    // ---------- shell ----------

    void RefreshUi() => panel.BeginInvoke(panel.Rebuild);

    void Save()
    {
        if (Demo.On) return; // demo data is never written
        profileSnapshot = State.Profiles.ToArray();
        forceInstanceRefresh = true;
        try { ProfileStore.Save(State); }
        catch (Exception ex) { Log.Error($"Save failed: {ex.Message}"); Dialogs.Error(DialogOwner, "Could not save profiles", ex.Message); }
    }

    public void ShowPanel() => panel.ShowNearTray();

    void TogglePanel()
    {
        // Clicking the tray icon first deactivates the flyout (which hides it); don't reopen it right away.
        if (DateTime.UtcNow - panel.LastAutoHideUtc < TimeSpan.FromMilliseconds(400)) return;
        if (panel.Visible && Form.ActiveForm == panel) panel.Hide();
        else ShowPanel();
    }

    void BuildTrayMenu()
    {
        var m = tray.ContextMenuStrip!;
        MenuUtil.Clear(m.Items);
        FluentMenuRenderer.Apply(m); // follows light/dark mode
        foreach (var p in State.Profiles)
        {
            var mem = Usage?.ByProfile.TryGetValue(p.Id, out var u) == true ? $"   ({UsageMonitor.Format(u!.TotalBytes)})" : "";
            var item = new ToolStripMenuItem(p.DesktopTitle + mem, IconFactory.ProfileImage(p, 16));
            item.DropDownItems.Add("Open desktop app", null, (_, _) => LaunchDesktop(p));
            item.DropDownItems.Add("Open terminal (last folder)", null, (_, _) => LaunchTerminal(p, null));
            item.DropDownItems.Add("Open terminal in…", null, (_, _) => PickFolderAndLaunchTerminal(p));
            m.Items.Add(item);
        }
        if (State.Profiles.Count > 0) m.Items.Add(new ToolStripSeparator());
        boldMenuFont ??= new Font(m.Font, FontStyle.Bold);
        var open = new ToolStripMenuItem("Open Claude Manager") { Font = boldMenuFont };
        open.Click += (_, _) => ShowPanel();
        m.Items.Add(open);
        m.Items.Add("Exit", null, (_, _) => Exit());
    }

    public void HandleCommand(string[] args, bool startup = false)
    {
        if (args.Length == 0) { ShowPanel(); return; }
        var cmd = args[0].ToLowerInvariant();
        if (cmd == "--tray") return;
        if (cmd == Ipc.ExitCommand) { Log.Info("Exit requested (installer/uninstaller)"); Exit(); return; }
        if (cmd == ProtocolHandler.HandleUrlArg)
        {
            if (args.Length > 1) RouteLink(args[1]);
            return;
        }
        if ((cmd == "--desktop" || cmd == "--terminal") && args.Length > 1)
        {
            var p = State.Find(args[1]);
            if (p == null)
            {
                Dialogs.Error(null, "Profile not found", "This shortcut points to a profile that no longer exists. Delete the shortcut or create a new one from Claude Manager.");
                return;
            }
            if (cmd == "--desktop") LaunchDesktop(p);
            else LaunchTerminal(p, args.Length > 2 ? args[2] : null);
            return;
        }
        ShowPanel();
    }

    public void StartDetection()
    {
        Desktop = null;
        Cli = null;
        RefreshUi();
        Task.Run(() =>
        {
            var d = DesktopApp.Detect();
            var c = ClaudeCli.Detect();
            panel.BeginInvoke(() =>
            {
                Desktop ??= d;
                Cli ??= c;
                if (Desktop.ExePath != null) IconFactory.ClaudeExe = Desktop.ExePath; // real Claude icon for profiles
                panel.Rebuild();
            });
        });
    }

    // Slow checks (WMI, PowerShell, claude --version) always run off the UI thread so the app never freezes.
    async Task<DesktopInstall> EnsureDesktopAsync()
    {
        if (Desktop == null || await Task.Run(() => DesktopApp.IsStale(Desktop)))
        {
            var old = Desktop?.ExePath;
            Desktop = await Task.Run(DesktopApp.Detect);
            if (old != null && Desktop.ExePath != old) Log.Info($"Claude desktop changed: {old} -> {Desktop.ExePath}");
            IconFactory.ClaudeExe = Desktop.ExePath;
            RefreshUi();
        }
        return Desktop;
    }

    async Task<CliInstall> EnsureCliAsync()
    {
        if (Cli == null || !Cli.Found || Cli.Unverified)
            Cli = await Task.Run(ClaudeCli.Detect);
        return Cli;
    }

    /// <summary>Ignores repeat clicks while the same launch is still in progress.</summary>
    readonly HashSet<string> busy = new();
    Font? boldMenuFont;

    public void Exit()
    {
        exiting = true;
        cts.Cancel();
        try { TrayTips.RestoreAll(); } catch { }
        signInForm?.Close();
        tray.Visible = false;
        tray.Dispose();
        panel.Dispose();
        Log.Info("Claude Manager exiting");
        ExitThread();
    }

    // ---------- window labels ----------

    volatile Profile[] profileSnapshot = Array.Empty<Profile>();
    volatile bool forceInstanceRefresh = true;

    public UsageSnapshot? Usage { get; private set; }

    /// <summary>
    /// Background loop (never on the UI thread), every 2 s:
    ///  - labels Claude window titles and Claude's tray-icon tooltips with the profile name,
    ///  - measures memory per profile (desktop process tree + terminals opened by Claude Manager).
    /// The slow process query (WMI) only re-runs when a desktop instance or terminal starts or stops.
    /// </summary>
    async Task MonitorLoop(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var instances = new List<DesktopInstance>();
        var terminals = new List<TerminalSession>();
        var lastSig = "";
        bool tipsApplied = false, usageShown = false;
        int tick = 0;
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            try
            {
                tick++;
                var profiles = profileSnapshot;
                bool changed = false;
                var sig = MonitorSignature();
                if (sig != lastSig || forceInstanceRefresh)
                {
                    forceInstanceRefresh = false;
                    lastSig = sig;
                    changed = true;
                    instances = DesktopWindows.FindInstances();
                    terminals = TerminalSessions.Find();
                }

                // Remember which profile's Claude window was used last; claude:// links usually belong to it.
                var fg = DesktopWindows.ForegroundPid();
                if (fg != 0 && instances.FirstOrDefault(i => i.Pid == fg) is { } fgInst
                            && DesktopWindows.ProfileFor(fgInst, profiles) is { } fgProfile)
                    NoteActive(fgProfile);

                if (State.LabelWindows)
                    foreach (var inst in instances)
                        if (DesktopWindows.ProfileFor(inst, profiles) is { } p) DesktopWindows.Label(inst, p);

                if (State.LabelTrayIcons)
                {
                    // Claude rarely resets its tooltip; re-apply on changes and every 10 s.
                    if (changed || tick % 5 == 0)
                        foreach (var inst in instances)
                            if (DesktopWindows.ProfileFor(inst, profiles) is { } p) TrayTips.Apply(inst.Pid, $"Claude ({p.Name})");
                    tipsApplied = true;
                }
                else if (tipsApplied) { TrayTips.RestoreAll(); tipsApplied = false; }

                if (State.ShowMemory)
                {
                    var usage = UsageMonitor.Compute(instances, terminals, profiles);
                    panel.BeginInvoke(() => ApplyUsage(usage));
                    usageShown = true;
                }
                else if (usageShown) { panel.BeginInvoke(() => ApplyUsage(null)); usageShown = false; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Error($"Monitor: {ex.Message}"); }
        }
    }

    void ApplyUsage(UsageSnapshot? usage)
    {
        Usage = usage;
        panel.UpdateUsage(usage);
        var text = usage == null ? "Claude Manager"
            : usage.RunningProfiles == 0 ? "Claude Manager: no profiles running"
            : $"Claude Manager: {usage.RunningProfiles} running, {UsageMonitor.Format(usage.TotalBytes)}";
        if (tray.Text != text) tray.Text = text.Length > 120 ? text[..120] : text;
    }

    /// <summary>Cheap fingerprint of desktop instances and cmd.exe processes (no WMI).</summary>
    static string MonitorSignature()
    {
        var claude = new List<int>();
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("claude")) using (p) claude.Add(p.Id);
        var ids = DesktopWindows.FilterDesktopPids(claude);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("cmd")) using (p) ids.Add(-p.Id);
        ids.Sort();
        return string.Join(",", ids);
    }

    public void SetLabelWindows(bool on) { State.LabelWindows = on; Save(); }
    public void SetLabelTrayIcons(bool on) { State.LabelTrayIcons = on; Save(); }
    public void SetShowMemory(bool on) { State.ShowMemory = on; Save(); RefreshUi(); }

    // ---------- first run ----------

    void RunFirstRun()
    {
        using var dlg = new FirstRunDialog();
        if (dlg.ShowDialog() != DialogResult.OK)
        {
            // Closed without choosing: create nothing, ask again next time.
            Log.Info("First run dialog closed without creating profiles");
            return;
        }
        State.Layout = dlg.FolderLayout;
        if (dlg.BackupFirst) BackupSettings();
        try
        {
            if (dlg.IncludeDefault) State.Profiles.Add(ProfileService.CreateDefault(dlg.DefaultName));
            foreach (var (name, color) in dlg.NewProfiles)
            {
                var (d, c) = ProfileService.SuggestFolders(State, name, State.Layout);
                ProfileService.Create(State, name, color, d, c);
            }
        }
        catch (Exception ex) { Dialogs.Error(null, "Could not create profile folders", ex.Message); }
        State.FirstRunDone = true;
        Save();
        ShowPanel();
        if (dlg.NewProfiles.Count > 0)
            Dialogs.Info(panel, "Profiles are ready",
                "Next: click Desktop on one new profile and finish its sign-in before opening the next one.\n\n" +
                "Click Terminal to start Claude Code in that profile, then type /login once.");
    }

    // ---------- desktop ----------

    public async void LaunchDesktop(Profile p)
    {
        if (!busy.Add("desktop:" + p.Id)) return;
        try { await LaunchDesktopCore(p); }
        catch (Exception ex)
        {
            Log.Launch(p.Name, "desktop", false, ex.Message);
            Dialogs.Error(DialogOwner, $"Could not open {p.DesktopTitle}", ex.Message);
        }
        finally { busy.Remove("desktop:" + p.Id); }
    }

    async Task LaunchDesktopCore(Profile p)
    {
        var d = await EnsureDesktopAsync();
        if (d.Kind == DesktopKind.NotFound)
        {
            Log.Launch(p.Name, "desktop", false, "desktop app not installed");
            var get = new TaskDialogButton("Open download page");
            if (Dialogs.Ask(DialogOwner, "Claude desktop is not installed",
                    "Install the Claude desktop app, then click Recheck in Claude Manager.", TaskDialogIcon.Warning, get, TaskDialogButton.Close) == get)
                Dialogs.OpenUrl(DesktopApp.DownloadUrl);
            return;
        }

        if (signInForm != null && pendingSignInId == p.Id) { signInForm.Activate(); return; }
        if (pendingSignInId != null && pendingSignInId != p.Id)
        {
            var pending = State.Find(pendingSignInId);
            var open = new TaskDialogButton("Open anyway");
            if (Dialogs.Ask(DialogOwner, "A sign-in is still in progress",
                    $"{pending?.DesktopTitle ?? "Another profile"} is waiting for its first sign-in. If you open another Claude window now, " +
                    "the sign-in link may go to the wrong account.\n\nFinish that sign-in first, or open anyway.",
                    TaskDialogIcon.Warning, open, TaskDialogButton.Cancel) != open)
                return;
        }

        // Already running (possibly closed to the tray)? Claude's own hand-off doesn't re-show a hidden
        // window, so restore that profile's window directly.
        var running = DesktopWindows.FindForProfile(p, await Task.Run(DesktopWindows.FindInstances));
        if (running != null)
        {
            var front = await Task.Run(() => DesktopWindows.BringToFront(running));
            forceInstanceRefresh = true; // re-label soon
            switch (front)
            {
                case DesktopWindows.FrontResult.Restored:
                    Log.Launch(p.Name, "desktop", true, $"already running pid={running.Pid}; window restored");
                    return;
                case DesktopWindows.FrontResult.NotResponding:
                    Log.Launch(p.Name, "desktop", false, $"pid={running.Pid} is not responding");
                    Dialogs.Info(DialogOwner, $"{p.DesktopTitle} is not responding",
                        "That Claude window is busy or frozen. Wait a moment and try again. If it stays frozen, end it from Task Manager; your other profiles are not affected.");
                    return;
                default:
                    Log.Info($"'{p.Name}' is running (pid={running.Pid}) but no window was found; asking Claude to show it");
                    break;
            }
        }

        bool firstSignIn = !p.IsDefault && !p.DesktopSignedIn && running == null;
        if (firstSignIn && !await ConfirmFirstSignInAsync(p)) return;

        try { ProfileService.EnsureFolders(p); Save(); }
        catch (Exception ex) { Dialogs.Error(DialogOwner, $"Could not prepare {p.DesktopTitle}", ex.Message); return; }

        var started = DateTime.UtcNow;
        var r = await Task.Run(() => DesktopApp.Launch(d, p));
        Log.Launch(p.Name, "desktop", r.Ok, r.Detail);
        if (r.Ok) NoteActive(p);
        if (!r.Ok)
        {
            Desktop = null;
            Dialogs.Error(DialogOwner, $"Could not open {p.DesktopTitle}",
                $"{r.Message}\n\nClick Recheck in Claude Manager, or reinstall the Claude desktop app. Details are in the log.");
            return;
        }

        if (firstSignIn)
        {
            pendingSignInId = p.Id;
            Save();
            ShowSignInForm(p, started);
        }
    }

    bool exiting;

    void ShowSignInForm(Profile p, DateTime startedUtc)
    {
        signInForm = new SignInWaitForm(p, startedUtc, LinkRoutingActive);
        signInForm.Finished += ok =>
        {
            signInForm = null;
            if (exiting) return; // keep the pending sign-in for the next start
            pendingSignInId = null;
            if (ok) { p.DesktopSignedIn = true; Log.Info($"Desktop sign-in confirmed for '{p.Name}'"); }
            Save();
            RefreshUi();
        };
        signInForm.Show();
    }

    /// <summary>After a restart mid-sign-in: keep routing links to that profile and show the helper again.</summary>
    void ResumePendingSignIn()
    {
        var id = pendingSignInId; // null if expired
        if (id == null)
        {
            if (State.PendingSignInId != null) { pendingSignInId = null; Save(); }
            return;
        }
        var p = State.Find(id);
        if (p == null || p.DesktopSignedIn) { pendingSignInId = null; Save(); return; }
        Log.Info($"Resuming pending first sign-in for '{p.Name}'");
        ShowSignInForm(p, State.PendingSignInUtc ?? DateTime.UtcNow);
    }

    async Task<bool> ConfirmFirstSignInAsync(Profile p)
    {
        EnsureLinkRouting();
        if (LinkRoutingActive)
        {
            // Links come to Claude Manager, which sends them to this profile while its sign-in is pending.
            var go = new TaskDialogButton($"Open {p.DesktopTitle}");
            return Dialogs.Ask(DialogOwner, $"First sign-in for {p.DesktopTitle}",
                $"Sign in with the account you use for {p.Name}.\n\nOther Claude windows can stay open: until you finish, " +
                $"Claude Manager sends sign-in links (including Google sign-in) to {p.DesktopTitle} only.",
                TaskDialogIcon.Information, go, TaskDialogButton.Cancel) == go;
        }

        while (true)
        {
            bool running = await Task.Run(DesktopApp.IsRunning);
            var open = new TaskDialogCommandLinkButton(
                running ? $"Open {p.DesktopTitle} anyway" : $"Open {p.DesktopTitle} for sign-in",
                running ? "Not recommended: the sign-in may land in the Claude window that is already open."
                        : "A new, empty Claude window opens for this profile.");
            var recheck = new TaskDialogCommandLinkButton("I've quit Claude, check again");
            var buttons = running
                ? new TaskDialogButton[] { recheck, open, TaskDialogButton.Cancel }
                : new TaskDialogButton[] { open, TaskDialogButton.Cancel };
            var text = $"Sign in with the account you use for {p.Name}.\n\n" +
                       "Sign-in links (claude://) open in whichever Claude window is already running, so only this new window " +
                       "should be open until the sign-in is done." +
                       (running ? "\n\nClaude desktop is running right now. Quit it first (right-click its tray icon, then Quit), then check again." : "");
            var r = Dialogs.Ask(DialogOwner, $"First sign-in for {p.DesktopTitle}", text,
                running ? TaskDialogIcon.Warning : TaskDialogIcon.Information, buttons);
            if (r == recheck) continue;
            return r == open;
        }
    }

    // ---------- claude:// link routing ----------

    readonly object routeGate = new();
    volatile string? lastActiveId;
    long lastActiveTicks;

    void NoteActive(Profile p)
    {
        lastActiveId = p.Id;
        Interlocked.Exchange(ref lastActiveTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>The profile whose Claude window was used within the given time, if any.</summary>
    Profile? RecentProfile(TimeSpan within)
    {
        var id = lastActiveId;
        if (id == null || DateTime.UtcNow - new DateTime(Interlocked.Read(ref lastActiveTicks), DateTimeKind.Utc) > within) return null;
        return State.Find(id);
    }

    public bool LinkRoutingActive
    {
        get
        {
            try { return State.RouteLinks && ProtocolHandler.IsCurrentlyOurs(ProtocolHandler.Read()); }
            catch { return false; }
        }
    }

    /// <summary>Points claude:// at Claude Manager (any thread). Saves Claude's own command for restore.</summary>
    void EnsureLinkRouting()
    {
        if (!State.RouteLinks || Demo.On) return;
        lock (routeGate)
        {
            string? replaced;
            try { replaced = ProtocolHandler.Install(); }
            catch (Exception ex) { Log.Error($"Could not route claude:// links: {ex.Message}"); return; }
            if (replaced == null) return;
            Log.Info($"claude:// links now go through Claude Manager (Claude's handler was: {replaced})");
            panel.BeginInvoke(() => { State.SavedProtocolCommand = replaced; Save(); });
        }
    }

    public async void SetRouteLinks(bool on)
    {
        State.RouteLinks = on;
        Save();
        if (on) { EnsureLinkRouting(); return; }

        lock (routeGate)
        {
            var original = State.SavedProtocolCommand;
            if (ProtocolHandler.ExeOf(original) == null && Desktop?.Kind == DesktopKind.Classic && Desktop.ExePath != null)
                original = ProtocolHandler.ClaudeCommandFor(Desktop.ExePath); // Claude updated since: use the current exe
            try
            {
                if (ProtocolHandler.Restore(original))
                {
                    Log.Info($"claude:// links handed back to Claude ({original})");
                    return;
                }
            }
            catch (Exception ex) { Log.Error($"Restoring claude:// handler failed: {ex.Message}"); }
        }
        await Task.Yield();
        Dialogs.Error(DialogOwner, "Could not hand links back to Claude",
            "Start the Claude desktop app once: it registers itself for claude:// links again.");
    }

    /// <summary>Delivers a claude:// link to the right profile's Claude instead of the Default one.</summary>
    async void RouteLink(string url)
    {
        if (!ProtocolHandler.IsValidUrl(url)) { Log.Error("Ignored an invalid claude:// link"); return; }
        var safe = ProtocolHandler.Redact(url); // never log sign-in codes
        try
        {
            var d = await EnsureDesktopAsync();
            if (d.Kind == DesktopKind.NotFound)
            {
                Dialogs.Error(null, "Claude desktop is not installed", "A claude:// link was opened, but the Claude desktop app could not be found.");
                return;
            }
            var target = await PickLinkTargetAsync();
            if (target == null) { Log.Info($"Link {safe}: cancelled"); return; }

            var r = await Task.Run(() => DesktopApp.Launch(d, target, url));
            Log.Launch(target.Name, "link", r.Ok, $"{safe} -> {r.Detail}");
            if (!r.Ok)
            {
                Dialogs.Error(null, $"Could not open the link in {target.DesktopTitle}", r.Message);
                return;
            }
            NoteActive(target);
            forceInstanceRefresh = true;
        }
        catch (Exception ex)
        {
            Log.Error($"Link {safe}: {ex.Message}");
            Dialogs.Error(null, "Could not open the Claude link", ex.Message);
        }
    }

    /// <summary>
    /// Which profile gets a link: the profile waiting for its first sign-in; else the only running one;
    /// else the one whose window was used in the last few minutes; otherwise the user picks.
    /// </summary>
    async Task<Profile?> PickLinkTargetAsync()
    {
        if (State.Profiles.Count == 0) return new Profile { IsDefault = true, Name = "Default" };
        if (pendingSignInId != null && State.Find(pendingSignInId) is { } pending) return pending;

        var instances = await Task.Run(DesktopWindows.FindInstances);
        var running = State.Profiles.Where(p => DesktopWindows.FindForProfile(p, instances) != null).ToList();
        var recent = RecentProfile(TimeSpan.FromMinutes(5));

        if (recent != null && running.Contains(recent)) return recent;
        if (running.Count == 1 && recent == null) return running[0];
        if (running.Count == 0 && State.Profiles.Count == 1) return State.Profiles[0];

        var order = State.Profiles
            .OrderByDescending(p => p == recent)
            .ThenByDescending(p => running.Contains(p))
            .ToList();
        var buttons = order.ToDictionary(p => (TaskDialogButton)new TaskDialogCommandLinkButton(p.DesktopTitle,
            p == recent ? "Used most recently" : running.Contains(p) ? "Running" : "Not running: it will start"), p => p);
        var choice = Dialogs.Ask(null, "Which profile should open this Claude link?",
            "A claude:// link (for example a sign-in) was opened. Pick the profile it belongs to.",
            TaskDialogIcon.Information, buttons.Keys.Append(TaskDialogButton.Cancel));
        return buttons.TryGetValue(choice, out var picked) ? picked : null;
    }

    public void SetSignedIn(Profile p, bool signedIn)
    {
        p.DesktopSignedIn = signedIn;
        Save();
        RefreshUi();
    }

    // ---------- terminal ----------

    public void PickFolderAndLaunchTerminal(Profile p)
    {
        using var fb = new FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = $"Project folder for {p.TerminalTitle}",
            InitialDirectory = p.RecentDirs.FirstOrDefault(Directory.Exists) ?? Paths.UserProfile,
        };
        if (fb.ShowDialog(DialogOwner) == DialogResult.OK) LaunchTerminal(p, fb.SelectedPath);
    }

    public async void LaunchTerminal(Profile p, string? dir)
    {
        if (!busy.Add("terminal:" + p.Id)) return;
        try { await LaunchTerminalCore(p, dir); }
        catch (Exception ex)
        {
            Log.Launch(p.Name, "terminal", false, ex.Message);
            Dialogs.Error(DialogOwner, $"Could not open {p.TerminalTitle}", ex.Message);
        }
        finally { busy.Remove("terminal:" + p.Id); }
    }

    async Task LaunchTerminalCore(Profile p, string? dir)
    {
        dir ??= p.RecentDirs.FirstOrDefault(Directory.Exists) ?? Paths.UserProfile;
        var cli = await EnsureCliAsync();
        if (!cli.Found)
        {
            Log.Launch(p.Name, "terminal", false, "Claude Code not installed");
            var install = new TaskDialogButton("Open install page");
            if (Dialogs.Ask(DialogOwner, "Claude Code is not installed",
                    "Claude Code was not found on your PATH. Install it, then click Recheck in Claude Manager.",
                    TaskDialogIcon.Warning, install, TaskDialogButton.Close) == install)
                Dialogs.OpenUrl(ClaudeCli.InstallUrl);
            return;
        }

        if (cli.TooOld)
        {
            var update = new TaskDialogCommandLinkButton("Run claude update", "Opens a terminal that updates Claude Code.");
            var anyway = new TaskDialogCommandLinkButton("Open anyway", "Safe for your existing setup.");
            var buttons = new List<TaskDialogButton> { update };
            if (p.IsDefault) buttons.Add(anyway);
            buttons.Add(TaskDialogButton.Cancel);
            var text = $"Claude Code is older than {ClaudeCli.MinVersion} (found {cli.Version}). Run claude update." +
                       (p.IsDefault ? "" :
                           "\n\nOlder versions read and write .claude.json in your user folder even when a profile folder is set, " +
                           "which could change your existing settings. Profile terminals stay blocked until Claude Code is updated.");
            var r = Dialogs.Ask(DialogOwner, "Claude Code needs an update", text, TaskDialogIcon.Warning, buttons);
            if (r == update) { RunCliUpdate(); return; }
            if (!p.IsDefault || r != anyway) { Log.Launch(p.Name, "terminal", false, $"blocked: Claude Code {cli.Version} too old"); return; }
        }

        if (cli.Unverified && !p.IsDefault)
        {
            // Unknown version could be an old one that writes ~\.claude.json despite CLAUDE_CONFIG_DIR.
            var retry = new TaskDialogCommandLinkButton("Check again");
            var update = new TaskDialogCommandLinkButton("Run claude update", "Opens a terminal that updates Claude Code.");
            var r = Dialogs.Ask(DialogOwner, "Claude Code's version could not be checked",
                $"\"claude --version\" did not report a version ({Shorten(cli.Raw)}). Versions older than {ClaudeCli.MinVersion} change your " +
                "existing settings even inside a profile, so profile terminals stay blocked until the version can be confirmed.",
                TaskDialogIcon.Warning, retry, update, TaskDialogButton.Cancel);
            if (r == update) { RunCliUpdate(); return; }
            if (r == retry)
            {
                Cli = null;
                RefreshUi();
                await LaunchTerminalCore(p, dir);
                return;
            }
            Log.Launch(p.Name, "terminal", false, "blocked: Claude Code version unknown");
            return;
        }

        bool clear = false;
        var vars = ClaudeCli.OverridingVars();
        if (vars.Count > 0)
        {
            if (State.AlwaysClearApiKey) clear = true;
            else
            {
                var clearBtn = new TaskDialogCommandLinkButton("Clear it for this terminal only", "Recommended. Your environment settings stay as they are.");
                var keep = new TaskDialogCommandLinkButton("Keep it", "Claude Code will use the key instead of this profile's login.");
                var always = new TaskDialogVerificationCheckBox("Always clear it without asking");
                var r = Dialogs.Ask(DialogOwner, $"{string.Join(", ", vars)} is set",
                    $"This variable takes priority over the login stored in the {p.Name} profile, so Claude Code would silently use it instead of the {p.Name} account.",
                    TaskDialogIcon.Warning, new TaskDialogButton[] { clearBtn, keep, TaskDialogButton.Cancel }, always);
                if (r == clearBtn)
                {
                    clear = true;
                    if (always.Checked) { State.AlwaysClearApiKey = true; Save(); }
                }
                else if (r != keep) return;
            }
        }

        try
        {
            bool firstUse = !p.IsDefault && ProfileService.IsEmptyDir(p.CliConfigDir);
            ProfileService.EnsureFolders(p);
            var started = DateTime.UtcNow;
            var workDir = dir;
            var detail = await Task.Run(() => Terminal.Open(p, workDir, clear));
            Log.Launch(p.Name, "terminal", true, detail);
            p.AddRecentDir(dir);
            Save();
            if (firstUse) WatchCliFolder(p, started);
        }
        catch (Exception ex)
        {
            Log.Launch(p.Name, "terminal", false, ex.Message);
            Dialogs.Error(DialogOwner, $"Could not open {p.TerminalTitle}",
                $"{ex.Message}\n\nCheck that Windows Terminal is installed, then try again.");
        }
    }

    static string Shorten(string? s)
    {
        s = (s ?? "no output").ReplaceLineEndings(" ").Trim();
        return s.Length == 0 ? "no output" : s.Length > 80 ? s[..80] + "…" : s;
    }

    /// <summary>On a profile's first terminal, confirm Claude Code writes to the profile folder.</summary>
    void WatchCliFolder(Profile p, DateTime startedUtc)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        int ticks = 0;
        timer.Tick += (_, _) =>
        {
            ticks++;
            if (ProfileService.FolderTouchedSince(p.CliConfigDir, startedUtc))
            {
                timer.Dispose();
                Log.Info($"Verified: Claude Code for '{p.Name}' writes to {p.CliConfigDir}");
                return;
            }
            if (ticks >= 30)
            {
                timer.Dispose();
                Log.Error($"Claude Code did not write to {p.CliConfigDir} within 90s for '{p.Name}'");
                tray.ShowBalloonTip(15000, $"Check {p.TerminalTitle}",
                    $"Claude Code has not used {Paths.Shorten(p.CliConfigDir!)} yet. Type /status in that terminal to check the account before you /login.",
                    ToolTipIcon.Warning);
            }
        };
        timer.Start();
    }

    public void RunCliUpdate()
    {
        try
        {
            var updater = new Profile { Id = "update", Name = "update", IsDefault = true, Color = "#828282" };
            var detail = Terminal.Open(updater, Paths.UserProfile, false, "claude update", "");
            Log.Info($"Started claude update: {detail}");
            Cli = null;
            Dialogs.Info(DialogOwner, "Updating Claude Code", "When the update in the terminal has finished, click Recheck in Claude Manager.");
        }
        catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not start claude update", ex.Message); }
    }

    // ---------- profiles ----------

    public void AddProfile()
    {
        using var dlg = new ProfileDialog(State, null);
        if (dlg.ShowDialog(DialogOwner) != DialogResult.OK) return;
        try
        {
            var created = ProfileService.Create(State, dlg.ProfileName, dlg.ProfileColor, dlg.DesktopDir, dlg.CliDir);
            created.Icon = dlg.ProfileIcon == IconFactory.ClaudeSpec ? null : dlg.ProfileIcon;
            Save();
            RefreshUi();
            Dialogs.Info(DialogOwner, $"Profile \"{dlg.ProfileName}\" created",
                "Click Desktop to do its first sign-in, or Terminal to start Claude Code and type /login.");
        }
        catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not create the profile", ex.Message); }
    }

    public void EditProfile(Profile p)
    {
        using var dlg = new ProfileDialog(State, p);
        if (dlg.ShowDialog(DialogOwner) != DialogResult.OK) return;
        p.Name = dlg.ProfileName;
        p.Color = dlg.ProfileColor;
        var newIcon = dlg.ProfileIcon == IconFactory.ClaudeSpec ? null : dlg.ProfileIcon;
        if (p.Icon != newIcon && p.Icon?.StartsWith(IconFactory.FilePrefix) == true)
        {
            // The old imported image is no longer used (shortcut .ico files are separate copies).
            var oldFile = p.Icon[IconFactory.FilePrefix.Length..];
            if (SafetyGuard.IsSameOrInside(oldFile, Paths.IconsDir)) try { File.Delete(oldFile); } catch { }
        }
        p.Icon = newIcon;
        if (!p.IsDefault)
        {
            var desk = Paths.Normalize(dlg.DesktopDir);
            if (!desk.Equals(p.DesktopDataDir, StringComparison.OrdinalIgnoreCase)) { p.DesktopDataDir = desk; p.DesktopSignedIn = false; }
            p.CliConfigDir = Paths.Normalize(dlg.CliDir);
            try { ProfileService.EnsureFolders(p); }
            catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not create the folder", ex.Message); }
        }
        Log.Info($"Edited profile '{p.Name}'");
        Save();
        RefreshUi();
    }

    public async void DeleteProfile(Profile p)
    {
        if (p.IsDefault || !busy.Add("delete:" + p.Id)) return;
        try { await DeleteProfileCore(p); }
        catch (Exception ex) { Dialogs.Error(DialogOwner, $"Could not delete \"{p.Name}\"", ex.Message); }
        finally { busy.Remove("delete:" + p.Id); }
    }

    async Task DeleteProfileCore(Profile p)
    {
        var sb = new StringBuilder();
        bool anyDeletable = false;
        foreach (var (label, dir) in new[] { ("Desktop data", p.DesktopDataDir), ("Claude Code", p.CliConfigDir) })
        {
            if (dir == null) continue;
            bool can = ProfileService.CanDeleteData(State, p, dir);
            anyDeletable |= can;
            var overlap = ProfileService.OverlappingProfile(State, dir, p);
            var note = !Directory.Exists(dir) ? "  (missing)"
                : can ? ""
                : overlap != null ? $"  (always kept: overlaps profile \"{overlap.Name}\")"
                : "  (always kept: not created by Claude Manager)";
            sb.AppendLine($"{label}: {dir}{note}");
        }
        sb.AppendLine();
        sb.Append("Your existing Claude setup is not affected either way.");

        var keep = new TaskDialogCommandLinkButton("Delete profile, keep its data", "The folders stay on disk. You can add them back later as a profile.");
        var wipe = new TaskDialogCommandLinkButton("Delete profile and its data", "Removes the folders above, including this account's login, chats and settings.")
        { Enabled = anyDeletable };
        var r = Dialogs.Ask(DialogOwner, $"Delete profile \"{p.Name}\"?", sb.ToString(), TaskDialogIcon.Warning, keep, wipe, TaskDialogButton.Cancel);
        if (r != keep && r != wipe) return;

        if (r == wipe)
        {
            // Only this profile's instance matters; other profiles keep running.
            var running = DesktopWindows.FindForProfile(p, await Task.Run(DesktopWindows.FindInstances));
            if (running != null)
            {
                var end = new TaskDialogCommandLinkButton($"End {p.DesktopTitle} and delete",
                    "Closes only this profile's Claude window. Your other profiles keep running.");
                if (Dialogs.Ask(DialogOwner, $"{p.DesktopTitle} is running",
                        "Its files are in use, so they can't be deleted while it runs.",
                        TaskDialogIcon.Warning, end, TaskDialogButton.Cancel) != end)
                    return;
                if (!await Task.Run(() => DesktopWindows.EndInstance(running, TimeSpan.FromSeconds(15))))
                {
                    Dialogs.Error(DialogOwner, $"{p.DesktopTitle} did not close", "Nothing was deleted. Quit it from its tray icon and try again.");
                    return;
                }
            }
        }

        if (pendingSignInId == p.Id) signInForm?.Close();
        List<string> notes;
        try { notes = await Task.Run(() => ProfileService.Delete(State, p, r == wipe)); }
        catch (ProfileInUseException ex)
        {
            Dialogs.Error(DialogOwner, $"\"{p.Name}\" was not deleted", ex.Message);
            return;
        }
        State.Profiles.Remove(p);
        Save();
        RefreshUi();
        if (notes.Count > 0) Dialogs.Info(DialogOwner, $"Profile \"{p.Name}\" deleted", string.Join("\n", notes));
    }

    public void CreateShortcuts(Profile p, bool startMenu)
    {
        try
        {
            var made = Shortcuts.Create(p, startMenu, IconFactory.EnsureProfileIco(p, false), IconFactory.EnsureProfileIco(p, true));
            Dialogs.Info(DialogOwner, "Shortcuts created", string.Join("\n", made));
        }
        catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not create shortcuts", ex.Message); }
    }

    public void OpenFolder(string? path)
    {
        if (path != null && Directory.Exists(path)) Dialogs.OpenFolder(path);
        else Dialogs.Info(DialogOwner, "Folder not found", $"{path ?? "(none)"} does not exist yet. It is created on first launch.");
    }

    // ---------- settings ----------

    public void BackupSettings()
    {
        try
        {
            Cursor.Current = Cursors.WaitCursor;
            var r = SettingsBackup.Create();
            Cursor.Current = Cursors.Default;
            var text = $"Saved {r.Files} files to:\n{r.ZipPath}\n\nLogin tokens were not copied." +
                       (r.SecretsRemoved > 0 ? $" {r.SecretsRemoved} secret values (API keys, tokens, MCP env/headers) were replaced with a placeholder." : "");
            if (r.Skipped.Count > 0) text += $"\n\nSkipped {r.Skipped.Count}:\n" + string.Join("\n", r.Skipped.Take(10));
            Dialogs.Info(DialogOwner, "Backup created", text);
        }
        catch (Exception ex)
        {
            Cursor.Current = Cursors.Default;
            Log.Error($"Backup failed: {ex.Message}");
            Dialogs.Error(DialogOwner, "Backup failed", ex.Message);
        }
    }

    public void ShowAbout()
    {
        var uninstall = new TaskDialogButton("Uninstall…");
        var license = new TaskDialogButton("View license");
        var r = Dialogs.Ask(DialogOwner, $"Claude Manager {About.Version}", About.Disclaimer +
            $"\n\nCopyright © 2026 Muhammad Adil. Free and open source under the MIT License.\nSettings and logs: {Paths.AppDir}",
            TaskDialogIcon.Information, license, uninstall, TaskDialogButton.OK);
        if (r == license)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
            if (File.Exists(file)) Dialogs.OpenUrl(file);
            else Dialogs.OpenUrl("https://github.com/madil9966/claude-manager/blob/main/LICENSE");
        }
        else if (r == uninstall)
        {
            // Run the uninstaller as its own process: it stops this instance first.
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Install.OwnExe, "--uninstall") { UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not start the uninstaller", ex.Message); }
        }
    }

    public void SetLayout(FolderLayout layout) { State.Layout = layout; Save(); }
    public void SetAlwaysClearApiKey(bool on) { State.AlwaysClearApiKey = on; Save(); }

    public void SetStartWithWindows(bool on)
    {
        try { StartupRegistration.Set(on); }
        catch (Exception ex) { Dialogs.Error(DialogOwner, "Could not change the startup setting", ex.Message); }
    }
}
