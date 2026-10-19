using ClaudeManager.Core;
using ClaudeManager.UI;

namespace ClaudeManager;

internal static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    const int ASFW_ANY = -1;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
            return SelfTest.Run(Console.Out);
        if (args.Length > 0 && args[0] == "--stress")
        {
            Application.EnableVisualStyles();
            return StressTest.Run(Console.Out);
        }
        if (args.Length > 0 && args[0] == "--uninstall")
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            return UninstallFlow.Run(quiet: args.Contains("--quiet"));
        }

        using var mutex = new Mutex(true, @"Local\ClaudeManager-" + Environment.UserName, out bool first);
        if (!first)
        {
            if (args.Length > 0 && args[0] == Ipc.ExitCommand) { Ipc.TrySend(args); return 0; }
            // Let the running instance bring its windows / Claude to the front (we were just launched by the user or browser).
            AllowSetForegroundWindow(ASFW_ANY);
            if (!Ipc.TrySend(args))
                MessageBox.Show(args.Length > 0 && args[0] == ProtocolHandler.HandleUrlArg
                        ? "A Claude sign-in link was opened, but Claude Manager is not responding, so the link was not delivered (it was not sent to your Default profile either). Close Claude Manager from the tray icon, start it again, and repeat the sign-in."
                        : "Claude Manager is already running but did not respond. Close it from the tray icon and try again.",
                    "Claude Manager", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 0;
        }

        if (args.Length > 0 && args[0] == Ipc.ExitCommand) return 0; // nothing running to stop

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) =>
        {
            Log.Error(e.Exception.ToString());
            MessageBox.Show($"{e.Exception.Message}\n\nDetails are in {Log.FilePath}", "Claude Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error(e.ExceptionObject?.ToString() ?? "unknown error");

        Log.Info($"Claude Manager starting, args=[{string.Join(" ", args.Select(a => ProtocolHandler.IsValidUrl(a) ? ProtocolHandler.Redact(a) : a))}]");
        Application.Run(new TrayApp(args));
        GC.KeepAlive(mutex);
        return 0;
    }
}
