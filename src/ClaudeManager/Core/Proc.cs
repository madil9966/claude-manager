using System.Diagnostics;

namespace ClaudeManager.Core;

public static class Proc
{
    public static (int ExitCode, string Output) Run(string file, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {file}");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
