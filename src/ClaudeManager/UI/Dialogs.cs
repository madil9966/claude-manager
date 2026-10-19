using System.Diagnostics;

namespace ClaudeManager.UI;

internal static class Dialogs
{
    public static TaskDialogButton Ask(IWin32Window? owner, string heading, string text, TaskDialogIcon icon,
        IEnumerable<TaskDialogButton> buttons, TaskDialogVerificationCheckBox? verification = null)
    {
        var page = new TaskDialogPage
        {
            Caption = "Claude Manager",
            Heading = heading,
            Text = text,
            Icon = icon,
            AllowCancel = true,
            SizeToContent = true,
        };
        foreach (var b in buttons) page.Buttons.Add(b);
        if (verification != null) page.Verification = verification;
        return owner == null
            ? TaskDialog.ShowDialog(page, TaskDialogStartupLocation.CenterScreen)
            : TaskDialog.ShowDialog(owner, page);
    }

    public static TaskDialogButton Ask(IWin32Window? owner, string heading, string text, TaskDialogIcon icon, params TaskDialogButton[] buttons) =>
        Ask(owner, heading, text, icon, (IEnumerable<TaskDialogButton>)buttons);

    public static void Error(IWin32Window? owner, string heading, string text) =>
        Ask(owner, heading, text, TaskDialogIcon.Error, TaskDialogButton.OK);

    public static void Info(IWin32Window? owner, string heading, string text) =>
        Ask(owner, heading, text, TaskDialogIcon.Information, TaskDialogButton.OK);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); } catch { }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); } catch { }
    }
}
