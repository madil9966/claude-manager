using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClaudeManager.Core;

/// <summary>
/// Routes claude:// links through Claude Manager so each link reaches the right profile.
///
/// Windows sends claude:// links to the command in HKCU\Software\Classes\claude\shell\open\command.
/// Claude registers itself there without --user-data-dir, so every link (including the sign-in
/// callback) would land in the Default profile. When routing is on, that one value points to
/// "ClaudeManager.exe --handle-url "%1"", and Claude Manager forwards the link to the right
/// profile's claude.exe. The original value is saved and written back when routing is turned off.
/// Claude re-registers itself on every start, so the value is watched and re-applied.
/// Only this registry value is changed; Claude's files are never touched.
/// </summary>
public static class ProtocolHandler
{
    public const string Scheme = "claude";
    public const string HandleUrlArg = "--handle-url";
    const string CommandKey = @"Software\Classes\" + Scheme + @"\shell\open\command";
    const string SchemeKey = @"Software\Classes\" + Scheme;
    const int MaxUrlLength = 8192;

    static string OwnExe => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find Claude Manager's own path.");
    public static string OurCommand => $"\"{OwnExe}\" {HandleUrlArg} \"%1\"";

    /// <summary>The current handler command, or null if Claude hasn't registered claude:// for this user.</summary>
    public static string? Read()
    {
        using var k = Registry.CurrentUser.OpenSubKey(CommandKey);
        return k?.GetValue(null) as string;
    }

    public static bool IsOurs(string? cmd) =>
        cmd != null && cmd.Contains(HandleUrlArg, StringComparison.OrdinalIgnoreCase)
                    && cmd.Contains("ClaudeManager", StringComparison.OrdinalIgnoreCase);

    public static bool IsCurrentlyOurs(string? cmd) => string.Equals(cmd, OurCommand, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Points claude:// at Claude Manager. Returns the replaced (Claude's) command so it can be
    /// saved for restore, or null if nothing was changed.
    /// </summary>
    public static string? Install()
    {
        var current = Read();
        if (current == null) return null;            // Claude never registered here (e.g. Store app): leave alone
        if (IsCurrentlyOurs(current)) return null;   // already routed
        using var k = Registry.CurrentUser.CreateSubKey(CommandKey);
        k.SetValue(null, OurCommand);
        return IsOurs(current) ? null : current;     // an old Claude Manager path is not worth saving
    }

    /// <summary>Writes back Claude's own handler. Only replaces the value if it is still ours.</summary>
    public static bool Restore(string? original)
    {
        if (!IsOurs(Read())) return true;
        if (string.IsNullOrWhiteSpace(original) || IsOurs(original)) return false;
        using var k = Registry.CurrentUser.CreateSubKey(CommandKey);
        k.SetValue(null, original);
        return true;
    }

    /// <summary>Clears our command when Claude can't be found to hand back to (Claude re-registers on its next start).</summary>
    public static void RemoveOurs()
    {
        if (!IsOurs(Read())) return;
        using var k = Registry.CurrentUser.OpenSubKey(CommandKey, writable: true);
        k?.DeleteValue("", false);
    }

    /// <summary>Builds Claude's normal handler command for an exe ("exe" "%1"), used when the saved one is stale.</summary>
    public static string ClaudeCommandFor(string exe) => $"\"{exe}\" \"%1\"";

    /// <summary>The exe in a handler command, if it still exists.</summary>
    public static string? ExeOf(string? cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return null;
        var c = cmd.Trim();
        var exe = c.StartsWith('"') ? c[1..].Split('"')[0] : c.Split(' ')[0];
        return exe.Length > 0 && File.Exists(exe) ? exe : null;
    }

    /// <summary>True for a single claude:// URL that is safe to pass on as one argument.</summary>
    public static bool IsValidUrl(string? url) =>
        url != null
        && url.Length <= MaxUrlLength
        && url.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase)
        && !url.Any(c => c == '"' || char.IsControl(c));

    /// <summary>The URL without its query/fragment (those carry sign-in codes and must never be logged).</summary>
    public static string Redact(string url)
    {
        var cut = url.IndexOfAny(new[] { '?', '#' });
        return cut < 0 ? url : url[..cut] + "?…";
    }

    // ---------- change watcher ----------

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, bool watchSubtree, int filter, SafeWaitHandle hEvent, bool async);
    const int REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    /// <summary>
    /// Calls <paramref name="onChange"/> (on a background thread) whenever the claude:// registration
    /// changes, and at least every few seconds as a fallback (also covers the key being created later).
    /// </summary>
    public static void Watch(Action onChange, CancellationToken token)
    {
        var t = new Thread(() =>
        {
            using var evt = new AutoResetEvent(false);
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey(SchemeKey);
                    if (k != null)
                        RegNotifyChangeKeyValue(k.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, evt.SafeWaitHandle, true);
                    WaitHandle.WaitAny(new[] { evt, token.WaitHandle }, TimeSpan.FromSeconds(5));
                    if (token.IsCancellationRequested) break;
                    onChange();
                }
                catch (Exception ex)
                {
                    Log.Error($"Link handler watch: {ex.Message}");
                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                }
            }
        }) { IsBackground = true, Name = "claude:// watcher" };
        t.Start();
    }
}
