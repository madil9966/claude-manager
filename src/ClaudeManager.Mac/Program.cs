using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ClaudeManager.Mac.Core;

namespace ClaudeManager.Mac;

/// <summary>
/// ClaudeManager                      menu bar app
/// ClaudeManager --selftest           safety checks (no data touched)
/// ClaudeManager --screenshot DIR     with CLAUDE_MANAGER_DEMO=1: render README screenshots, then exit
/// </summary>
internal static class Program
{
    static FileStream? instanceLock;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return MacSelfTest.Run(Console.Out);

        if (OperatingSystem.IsMacOS() && !MacDemo.On && !args.Contains("--screenshot"))
        {
            // One instance per user (macOS usually ensures this already for apps started from Finder).
            try
            {
                Directory.CreateDirectory(MacPaths.AppDir);
                instanceLock = new FileStream(Path.Combine(MacPaths.AppDir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) { return 0; }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false }) // menu bar app
            .LogToTrace();
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default; // follows macOS light/dark appearance
        Name = "Claude Manager";
    }

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var controller = new AppController(desktop);
        var args = desktop.Args ?? Array.Empty<string>();
        int i = Array.IndexOf(args, "--screenshot");
        if (i >= 0)
        {
            await controller.RenderScreenshots(i + 1 < args.Length ? args[i + 1] : "screenshots");
            controller.Quit();
            return;
        }
        controller.ShowMain();
    }
}
