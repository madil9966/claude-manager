using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClaudeManager.Core;

/// <summary>Single-instance command channel (shortcuts and second launches talk to the running tray app).</summary>
public static class Ipc
{
    /// <summary>
    /// Per user and per Windows session: pipe names are machine-wide, while the single-instance mutex is
    /// per session, so two sessions of the same user (RDP + console) would otherwise collide.
    /// Keep in sync with the installer (ClaudeManager.Setup/Installer.cs).
    /// </summary>
    public static string PipeName => $"ClaudeManager-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    public const string ExitCommand = "--exit";

    public static bool TrySend(string[] args, int timeoutMs = 3000)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            c.Connect(timeoutMs);
            using var w = new StreamWriter(c);
            w.WriteLine(JsonSerializer.Serialize(args));
            w.Flush();
            return true;
        }
        catch { return false; }
    }

    public static void StartServer(Action<string[]> onCommand, CancellationToken token)
    {
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await s.WaitForConnectionAsync(token);
                    using var r = new StreamReader(s);
                    var line = await r.ReadLineAsync(token);
                    var args = line == null ? Array.Empty<string>() : JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
                    onCommand(args);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Error($"IPC: {ex.Message}");
                    await Task.Delay(500);
                }
            }
        });
    }
}

public static class StartupRegistration
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string Name = "ClaudeManager";

    public static bool IsEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string;
    }

    public static void Set(bool on) => SetFor(on ? Environment.ProcessPath : null);

    /// <summary>Points "Start with Windows" at the given exe, or removes it (null).</summary>
    public static void SetFor(string? exe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (exe != null) k.SetValue(Name, $"\"{exe}\" --tray");
        else k.DeleteValue(Name, false);
    }

    /// <summary>The exe the startup entry points to, if any.</summary>
    public static string? RegisteredExe()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(Name) is string v ? ProtocolHandler.ExeOf(v) ?? ExtractExe(v) : null;
    }

    static string ExtractExe(string cmd)
    {
        var c = cmd.Trim();
        return c.StartsWith('"') ? c[1..].Split('"')[0] : c.Split(' ')[0];
    }
}

public static class Shortcuts
{
    public static List<string> Create(Profile p, bool startMenu, string desktopIcon, string terminalIcon)
    {
        var folder = startMenu
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Manager")
            : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        Directory.CreateDirectory(folder);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Claude Manager's own path.");
        return new List<string>
        {
            Make(Path.Combine(folder, Safe(p.DesktopTitle) + ".lnk"), exe, $"--desktop {p.Id}", desktopIcon, $"Open {p.DesktopTitle}"),
            Make(Path.Combine(folder, Safe(p.TerminalTitle) + ".lnk"), exe, $"--terminal {p.Id}", terminalIcon, $"Open {p.TerminalTitle} in Windows Terminal"),
        };
    }

    public static string StartMenuFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Manager");
    public static string DesktopFolder => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>The installer's own shortcut name ("Claude Manager.lnk", opens the panel).</summary>
    public const string AppShortcutName = "Claude Manager.lnk";

    /// <summary>
    /// Every shortcut Claude Manager may have made: profile shortcuts in the Desktop and the
    /// Start-menu "Claude Manager" folder, and the app shortcut on the Desktop / in Start menu.
    /// Only .lnk files whose target is a ClaudeManager.exe are returned, with that target.
    /// </summary>
    public static List<(string Lnk, string Target)> FindOwn()
    {
        var found = new List<(string, string)>();
        var candidates = new List<string>();
        foreach (var dir in new[] { DesktopFolder, StartMenuFolder })
            if (Directory.Exists(dir)) candidates.AddRange(Directory.GetFiles(dir, "*.lnk"));
        var rootLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppShortcutName);
        if (File.Exists(rootLnk)) candidates.Add(rootLnk);
        if (candidates.Count == 0) return found;

        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return found;
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            foreach (var lnk in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    dynamic sc = shell.CreateShortcut(lnk);
                    string target = sc.TargetPath;
                    Marshal.FinalReleaseComObject(sc);
                    if (Path.GetFileName(target).Equals("ClaudeManager.exe", StringComparison.OrdinalIgnoreCase)) found.Add((lnk, target));
                }
                catch { /* unreadable shortcut: not ours to touch */ }
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return found;
    }

    /// <summary>Points an existing shortcut at a new exe (keeps its arguments and icon).</summary>
    public static void Retarget(string lnk, string exe)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe);
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    static string Make(string lnk, string target, string args, string icon, string description)
    {
        SafetyGuard.AssertWritable(lnk);
        var t = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.Arguments = args;
            sc.IconLocation = icon + ",0";
            sc.Description = description;
            sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Save();
            Marshal.FinalReleaseComObject(sc);
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        Log.Info($"Shortcut created: {lnk}");
        return lnk;
    }
}
