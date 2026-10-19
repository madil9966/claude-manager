using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>
/// Stays on top during a profile's first desktop sign-in. It also checks that Claude really
/// writes to the profile folder; if not, Claude may have opened the existing account instead,
/// and the user is told not to sign in there.
/// </summary>
internal sealed class SignInWaitForm : Form
{
    public event Action<bool>? Finished;

    readonly Profile profile;
    readonly DateTime launchedUtc;
    readonly Label status = new() { AutoSize = true };
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    bool verified, reported;

    int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public SignInWaitForm(Profile p, DateTime launchedUtc, bool linksRouted = false)
    {
        profile = p;
        this.launchedUtc = launchedUtc;
        Text = $"First sign-in: {p.DesktopTitle}";
        Font = SystemFonts.MessageBoxFont ?? Font;
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(S(12));

        int w = S(400);
        var col = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Location = new Point(S(12), S(12)) };
        var dot = IconFactory.Parse(p.Color);
        col.Controls.Add(new Label { Text = $"Sign in to the {p.Name} account", AutoSize = true, Font = new Font(Font.FontFamily, Font.Size + 2, FontStyle.Bold), ForeColor = dot });
        col.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(w, 0), Margin = new Padding(0, S(6), 0, S(6)),
            Text = $"A new Claude window opened for {p.Name}. Sign in there with the account you use for {p.Name}.\n\n" +
                   (linksRouted
                       ? $"Until you finish, Claude Manager sends sign-in links (including Google sign-in) to this window. Don't start another profile's sign-in meanwhile."
                       : "Until you finish, don't open any other Claude window: sign-in links go to whichever Claude window is open."),
        });
        status.MaximumSize = new Size(w, 0);
        status.Text = "Checking that Claude is using this profile's folder…";
        status.ForeColor = SystemColors.GrayText;
        col.Controls.Add(status);

        var done = new Button { Text = "I'm signed in", AutoSize = true };
        var later = new Button { Text = "Not now", AutoSize = true };
        done.Click += (_, _) =>
        {
            if (!verified)
            {
                var yes = new TaskDialogButton("Mark as signed in anyway");
                if (Dialogs.Ask(this, "Claude didn't use the profile folder",
                        $"Claude Manager saw no activity in {p.DesktopDataDir}. The window you signed into may be your existing account.\n\nCheck which account that window shows before continuing.",
                        TaskDialogIcon.Warning, yes, TaskDialogButton.Cancel) != yes)
                    return;
            }
            Complete(true);
        };
        later.Click += (_, _) => Complete(false);
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, S(10), 0, 0) };
        buttons.Controls.Add(done);
        buttons.Controls.Add(later);
        col.Controls.Add(buttons);
        Controls.Add(col);

        timer.Tick += (_, _) => Check();
        FormClosed += (_, _) => { timer.Dispose(); if (!reported) { reported = true; Finished?.Invoke(false); } };
        Load += (_, _) =>
        {
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
            Location = new Point(wa.Right - Width - S(16), wa.Bottom - Height - S(16));
            timer.Start();
        };
    }

    void Check()
    {
        if (verified) return;
        if (ProfileService.FolderTouchedSince(profile.DesktopDataDir, launchedUtc))
        {
            verified = true;
            timer.Stop();
            status.Text = $"✓ Claude is using the {profile.Name} folder ({Paths.Shorten(profile.DesktopDataDir!)}).";
            status.ForeColor = Color.ForestGreen;
            Log.Info($"Verified: desktop for '{profile.Name}' writes to {profile.DesktopDataDir}");
        }
        else if (DateTime.UtcNow - launchedUtc > TimeSpan.FromSeconds(25))
        {
            status.Text = $"⚠ Claude has not written to {profile.DesktopDataDir}. If the new window shows an account you're already signed in to, " +
                          "it ignored the profile folder. Don't sign in there; close it and check the log.";
            status.ForeColor = Color.Firebrick;
            if (DateTime.UtcNow - launchedUtc > TimeSpan.FromSeconds(120)) timer.Stop();
        }
    }

    void Complete(bool signedIn)
    {
        if (reported) return;
        reported = true;
        Finished?.Invoke(signedIn);
        Close();
    }
}
