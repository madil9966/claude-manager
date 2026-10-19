using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>ClaudeManager.exe --uninstall [--quiet]. Also what Apps &amp; Features runs.</summary>
internal static class UninstallFlow
{
    public static int Run(bool quiet)
    {
        bool removeSettings = false;
        if (!quiet)
        {
            var keepData = new TaskDialogVerificationCheckBox("Also delete Claude Manager's own settings and logs (profile list, launch scripts, icons)");
            var go = new TaskDialogButton("Uninstall");
            var text =
                "This removes Claude Manager's shortcuts, its \"Start with Windows\" entry and its claude:// link handling " +
                "(links go straight to Claude again)" + (Install.IsInstalledCopy ? ", and its program files." : ".") +
                "\n\nYour Claude accounts are NOT deleted: profile folders, logins, chats and your existing Claude setup all stay on disk. " +
                "To remove a profile's data, use Delete profile in Claude Manager before uninstalling." +
                $"\n\n{About.ShortDisclaimer}";
            if (Dialogs.Ask(null, "Uninstall Claude Manager?", text, TaskDialogIcon.Warning,
                    new TaskDialogButton[] { go, TaskDialogButton.Cancel }, keepData) != go)
                return 1;
            removeSettings = keepData.Checked;
        }

        if (!Install.StopRunningInstance(TimeSpan.FromSeconds(8)))
        {
            if (!quiet) Dialogs.Error(null, "Claude Manager is still running", "Exit it from its tray icon, then run the uninstaller again.");
            return 2;
        }

        string? claudeExe = null;
        try { claudeExe = DesktopApp.Detect().ExePath; } catch { }
        var r = Install.Uninstall(removeSettings, claudeExe);

        if (!quiet)
        {
            var body = string.Join("\n", r.Done.Select(d => "✓ " + d));
            if (r.Problems.Count > 0) body += "\n\nCould not finish:\n" + string.Join("\n", r.Problems.Select(p => "• " + p));
            if (!removeSettings) body += $"\n\nYour settings were kept in {Paths.AppDir}; reinstalling picks them up again.";
            if (r.Done.Count == 0 && r.Problems.Count == 0) body = "Nothing to remove.";
            Dialogs.Ask(null, r.Problems.Count == 0 ? "Claude Manager was uninstalled" : "Uninstall finished with problems", body,
                r.Problems.Count == 0 ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Warning, TaskDialogButton.OK);
        }
        return r.Problems.Count == 0 ? 0 : 3;
    }
}
