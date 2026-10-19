using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClaudeManager.Core;

public enum DesktopKind { NotFound, Classic, Packaged }

public sealed record DesktopInstall(DesktopKind Kind, string? ExePath, string? Aumid, string? Version)
{
    public string Describe()
    {
        var v = Version is null ? "" : " " + Version;
        return Kind switch
        {
            DesktopKind.Classic => $"Claude desktop{v} (classic install)",
            DesktopKind.Packaged => $"Claude desktop{v} (Microsoft Store / MSIX)",
            _ => "Claude desktop: not installed",
        };
    }
}

/// <summary>Finds and launches the Claude desktop app. Never modifies it.</summary>
public static class DesktopApp
{
    public const string DownloadUrl = "https://claude.ai/download";
    static string Stub => Path.Combine(Paths.LocalAppData, "Microsoft", "WindowsApps", "Claude.exe");

    public static DesktopInstall Detect()
    {
        DesktopInstall result;
        try
        {
            if (File.Exists(Stub))
            {
                var (aumid, ver) = QueryPackage();
                result = new(DesktopKind.Packaged, Stub, aumid, ver);
            }
            else if (FindClassic(out var cver) is { } exe)
            {
                result = new(DesktopKind.Classic, exe, null, cver);
            }
            else
            {
                var (aumid, ver) = QueryPackage();
                result = aumid != null ? new(DesktopKind.Packaged, null, aumid, ver) : new(DesktopKind.NotFound, null, null, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Desktop detection failed: {ex.Message}");
            result = new(DesktopKind.NotFound, null, null, null);
        }
        Log.Info($"Desktop detected: {result.Kind} exe={result.ExePath} aumid={result.Aumid} version={result.Version}");
        return result;
    }

    /// <summary>
    /// True when a classic install's exe is gone or a newer app-* version is installed (Claude updated
    /// itself while Claude Manager was running). Old version folders stay on disk, so without this check
    /// profiles would keep starting an old Claude on data written by a newer one. Cheap: one folder listing.
    /// </summary>
    public static bool IsStale(DesktopInstall inst)
    {
        if (inst.Kind != DesktopKind.Classic) return false;
        if (inst.ExePath == null || !File.Exists(inst.ExePath)) return true;
        try { return !string.Equals(FindClassic(out _), inst.ExePath, StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static string? FindClassic(out string? version)
    {
        version = null;
        var root = Path.Combine(Paths.LocalAppData, "AnthropicClaude");
        if (Directory.Exists(root))
        {
            var best = Directory.GetDirectories(root, "app-*")
                .Select(d => (dir: d, ver: System.Version.TryParse(Path.GetFileName(d)[4..], out var v) ? v : null))
                .Where(x => x.ver != null && File.Exists(Path.Combine(x.dir, "claude.exe")))
                .OrderByDescending(x => x.ver)
                .FirstOrDefault();
            if (best.dir != null)
            {
                version = best.ver!.ToString();
                return Path.Combine(best.dir, "claude.exe");
            }
            var stub = Path.Combine(root, "claude.exe");
            if (File.Exists(stub)) return stub;
        }
        foreach (var c in new[]
                 {
                     Path.Combine(Paths.LocalAppData, "Programs", "Claude", "Claude.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Claude", "Claude.exe"),
                 })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>Looks up the packaged app's AUMID and version through PowerShell.</summary>
    static (string? Aumid, string? Version) QueryPackage()
    {
        const string script = """
            $ErrorActionPreference = 'SilentlyContinue'
            $p = Get-AppxPackage | Where-Object { $_.Name -like '*Claude*' -and $_.Name -notlike '*Code*' } | Select-Object -First 1
            $a = Get-StartApps | Where-Object { $_.AppID -like '*!*' -and $_.Name -like 'Claude*' -and $_.Name -notlike '*Code*' } | Select-Object -First 1
            $id = $a.AppID
            if (-not $id -and $p) { $app = (Get-AppxPackageManifest $p).Package.Applications.Application | Select-Object -First 1; if ($app) { $id = "$($p.PackageFamilyName)!$($app.Id)" } }
            Write-Output "$id|$($p.Version)"
            """;
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var (_, output) = Proc.Run("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", 20000);
            var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('|')) ?? "|";
            var parts = line.Split('|');
            return (string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0], parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null);
        }
        catch (Exception ex)
        {
            Log.Error($"Package query failed: {ex.Message}");
            return (null, null);
        }
    }

    public static bool IsRunning()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                try
                {
                    var f = p.MainModule?.FileName;
                    if (f != null && (f.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase)
                                      || f.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)
                                      || f.EndsWith(@"\Claude\Claude.exe", StringComparison.OrdinalIgnoreCase)))
                        return true;
                }
                catch { /* access denied or exited */ }
            }
        }
        return false;
    }

    /// <summary>Default profile: no flag (app's normal data). Others: --user-data-dir=profile folder.</summary>
    /// <param name="url">Optional claude:// link. If the profile is already running, Claude passes it to that window.</param>
    public static string BuildArgs(Profile p, string? url = null)
    {
        var args = p.IsDefault || string.IsNullOrEmpty(p.DesktopDataDir) ? "" : $"--user-data-dir=\"{p.DesktopDataDir}\"";
        if (url != null)
        {
            if (!ProtocolHandler.IsValidUrl(url)) throw new ArgumentException("Not a valid claude:// link.", nameof(url));
            args = (args + $" \"{url}\"").TrimStart();
        }
        return args;
    }

    public static LaunchResult Launch(DesktopInstall inst, Profile p, string? url = null)
    {
        if (!p.IsDefault)
        {
            // Defence in depth: a non-default profile must never resolve to the existing data folder.
            var err = SafetyGuard.CheckProfileFolder(p.DesktopDataDir ?? "");
            if (err != null) return new(false, $"The profile folder {err}", "blocked by safety guard");
        }
        var args = BuildArgs(p, url);
        var logArgs = url == null ? args : BuildArgs(p) + $" \"{ProtocolHandler.Redact(url)}\"";
        try
        {
            switch (inst.Kind)
            {
                case DesktopKind.Classic:
                    Process.Start(new ProcessStartInfo(inst.ExePath!, args)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(inst.ExePath!)!,
                    })?.Dispose();
                    return new(true, "", $"classic exe={inst.ExePath} args={logArgs}");

                case DesktopKind.Packaged:
                    string? stubError = null;
                    if (inst.ExePath != null && File.Exists(inst.ExePath))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(inst.ExePath, args) { UseShellExecute = false })?.Dispose();
                            return new(true, "", $"packaged stub={inst.ExePath} args={logArgs}");
                        }
                        catch (Exception ex) { stubError = ex.Message; }
                    }
                    var aumid = inst.Aumid ?? QueryPackage().Aumid;
                    if (aumid == null)
                        return new(false, "The Microsoft Store version of Claude could not be started.", $"stub error={stubError}; no AUMID");
                    var pid = AppActivation.Activate(aumid, args);
                    return new(true, "", $"packaged aumid={aumid} pid={pid} args={logArgs} (stub error={stubError})");

                default:
                    return new(false, "The Claude desktop app is not installed.", "not found");
            }
        }
        catch (Exception ex)
        {
            return new(false, ex.Message, $"{inst.Kind} exception: {ex.Message}");
        }
    }
}

/// <summary>Starts a packaged app by AUMID with arguments (IApplicationActivationManager).</summary>
internal static class AppActivation
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                                [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    class ApplicationActivationManager { }

    public static uint Activate(string aumid, string args)
    {
        var mgr = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            Marshal.ThrowExceptionForHR(mgr.ActivateApplication(aumid, args, 0, out var pid));
            return pid;
        }
        finally { Marshal.ReleaseComObject(mgr); }
    }
}
