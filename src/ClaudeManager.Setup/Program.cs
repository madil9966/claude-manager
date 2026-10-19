namespace ClaudeManager.Setup;

/// <summary>
/// ClaudeManager-Setup.exe                 wizard
/// ClaudeManager-Setup.exe /S              silent install with defaults (also --silent)
///   options for silent: --dir="C:\path"  --no-desktop  --no-startmenu  --no-startup  --launch
/// Exit codes: 0 ok, 1 cancelled/invalid folder, 2 install failed.
/// </summary>
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
        if (silent) return Silent(args);

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
        return 0;
    }

    static int Silent(string[] args)
    {
        bool Has(string a) => args.Any(x => x.Equals(a, StringComparison.OrdinalIgnoreCase));
        var dirArg = args.FirstOrDefault(a => a.StartsWith("--dir=", StringComparison.OrdinalIgnoreCase));
        var dir = dirArg != null ? dirArg[6..].Trim('"') : Installer.FindExisting()?.Dir ?? Installer.DefaultDir;
        if (Installer.CheckDir(dir) is { } err) { Console.Error.WriteLine(err); return 1; }
        try
        {
            var exe = Installer.Install(new InstallOptions(dir, !Has("--no-desktop"), !Has("--no-startmenu"), !Has("--no-startup")), Console.WriteLine);
            if (Has("--launch")) Installer.Launch(exe);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Install failed: {ex.Message}"); return 2; }
    }

    /// <summary>Runs work on a dedicated STA thread (WScript.Shell shortcuts) and returns its result.</summary>
    public static T RunSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        return result;
    }
}
