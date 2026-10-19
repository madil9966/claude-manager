using System.Diagnostics;
using System.Runtime.InteropServices;
using ClaudeManager.Core;

namespace ClaudeManager.UI;

/// <summary>
/// ClaudeManager.exe --stress > result.txt
/// Repeats the code paths that run while profiles are open (process lookup, window labeling,
/// menu rebuilds) and reports memory, handle and GDI/USER object growth. No windows are restored
/// and nothing is launched.
/// </summary>
internal static class StressTest
{
    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr hProcess, int flags);

    record Snapshot(long Managed, long Private, int Handles, int Gdi, int User);

    static Snapshot Take()
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var me = Process.GetCurrentProcess();
        me.Refresh();
        return new(GC.GetTotalMemory(true), me.PrivateMemorySize64, me.HandleCount,
            GetGuiResources(me.Handle, 0), GetGuiResources(me.Handle, 1));
    }

    static void Print(TextWriter o, string label, Snapshot a, Snapshot b) =>
        o.WriteLine($"{label,-28} managed {a.Managed / 1024,6} -> {b.Managed / 1024,6} KB | private {a.Private / 1048576,4} -> {b.Private / 1048576,4} MB | " +
                    $"handles {a.Handles,4} -> {b.Handles,4} | GDI {a.Gdi,3} -> {b.Gdi,3} | USER {a.User,3} -> {b.User,3}");

    public static int Run(TextWriter o)
    {
        var state = ProfileStore.Load();
        var profiles = state.Profiles.ToArray();
        o.WriteLine($"Profiles: {string.Join(", ", profiles.Select(p => p.Name))}");

        // Warm-up (JIT, WMI connection, caches) so growth numbers reflect steady state.
        for (int i = 0; i < 5; i++) DesktopWindows.FindInstances();

        // 1. Process lookup + window labeling, as the background loop does.
        var before = Take();
        var sw = Stopwatch.StartNew();
        const int lookups = 60;
        int found = 0;
        for (int i = 0; i < lookups; i++)
        {
            var ids = Process.GetProcessesByName("claude").Select(p => { using (p) return p.Id; }).ToList();
            DesktopWindows.FilterDesktopPids(ids);
            var inst = DesktopWindows.FindInstances();
            found = inst.Count;
            foreach (var x in inst)
                if (DesktopWindows.ProfileFor(x, profiles) is { } p) DesktopWindows.Label(x, p);
        }
        sw.Stop();
        var after = Take();
        o.WriteLine($"Claude desktop instances seen: {found}");
        o.WriteLine($"Process lookup + labeling: {lookups} runs, avg {sw.Elapsed.TotalMilliseconds / lookups:F1} ms (runs in background, not on UI thread)");
        Print(o, $"lookup/label x{lookups}", before, after);
        bool okLookup = after.Handles - before.Handles < 30 && after.Managed - before.Managed < 2_000_000 && after.Gdi - before.Gdi < 5 && after.User - before.User < 5;

        // 2. Cheap signature (runs every 2 s).
        before = Take();
        sw.Restart();
        for (int i = 0; i < 2000; i++)
        {
            var ids = Process.GetProcessesByName("claude").Select(p => { using (p) return p.Id; }).ToList();
            DesktopWindows.FilterDesktopPids(ids);
        }
        sw.Stop();
        after = Take();
        o.WriteLine($"Signature check: avg {sw.Elapsed.TotalMilliseconds / 2000:F2} ms");
        Print(o, "signature x2000", before, after);
        bool okSig = after.Handles - before.Handles < 30 && after.Managed - before.Managed < 2_000_000;

        // 2b. Memory sampling (runs every 2 s while "Show memory usage" is on).
        var insts = DesktopWindows.FindInstances();
        var terms = TerminalSessions.Find();
        for (int i = 0; i < 20; i++) UsageMonitor.Compute(insts, terms, profiles); // warm-up
        before = Take();
        sw.Restart();
        UsageSnapshot? last = null;
        for (int i = 0; i < 1000; i++) last = UsageMonitor.Compute(insts, terms, profiles);
        sw.Stop();
        after = Take();
        o.WriteLine($"Memory sampling: avg {sw.Elapsed.TotalMilliseconds / 1000:F2} ms, terminals found {terms.Count}");
        foreach (var (id, u) in last!.ByProfile)
            o.WriteLine($"   {profiles.First(p => p.Id == id).Name}: {UsageMonitor.Describe(u)}");
        Print(o, "memory sampling x1000", before, after);
        bool okUsage = after.Handles - before.Handles < 20 && after.Managed - before.Managed < 1_000_000 && after.Private - before.Private < 20_000_000;

        // 2c. Tray tooltips (re-applied every 10 s).
        for (int i = 0; i < 3; i++) foreach (var x in insts) if (DesktopWindows.ProfileFor(x, profiles) is { } tp) TrayTips.Apply(x.Pid, $"Claude ({tp.Name})");
        before = Take();
        for (int i = 0; i < 500; i++)
            foreach (var x in insts) if (DesktopWindows.ProfileFor(x, profiles) is { } tp) TrayTips.Apply(x.Pid, $"Claude ({tp.Name})");
        after = Take();
        Print(o, "tray tooltips x500", before, after);
        bool okTips = after.Handles - before.Handles < 20 && after.Gdi - before.Gdi < 3 && after.User - before.User < 3;
        okSig = okSig && okUsage && okTips;

        // 3. Menu rebuilds (tray menu / profile menus are rebuilt on every open).
        using var menu = new ContextMenuStrip();
        using var bold = new Font(menu.Font, FontStyle.Bold);
        void Rebuild(int n)
        {
            for (int i = 0; i < n; i++)
            {
                MenuUtil.Clear(menu.Items);
                foreach (var p in profiles)
                {
                    var item = new ToolStripMenuItem(p.DesktopTitle, IconFactory.MenuDot(p.Color));
                    item.DropDownItems.Add("Open desktop app");
                    item.DropDownItems.Add("Open terminal");
                    menu.Items.Add(item);
                }
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Open Claude Manager") { Font = bold });
            }
            MenuUtil.Clear(menu.Items);
        }
        Rebuild(50); // warm-up: renderer/font caches
        var m0 = Take();
        Rebuild(1000);
        var m1 = Take();
        Rebuild(1000);
        var m2 = Take();
        Print(o, "menu rebuild 1st x1000", m0, m1);
        Print(o, "menu rebuild 2nd x1000", m1, m2);
        // A leak grows in every batch; one-time caching doesn't.
        bool okMenu = m2.Gdi - m1.Gdi < 3 && m2.User - m1.User < 3 && m2.Handles - m1.Handles < 20 && m2.Managed - m1.Managed < 1_000_000;

        // 4. Profile icons: every preset + the Claude icon, rendered fresh (dialog previews, shortcuts).
        IconFactory.ClaudeExe = DesktopApp.Detect().ExePath;
        var specs = new List<string> { IconFactory.ClaudeSpec };
        specs.AddRange(IconFactory.Presets.Select(p => IconFactory.PresetPrefix + p.Key));
        int[] sizes = { 16, 20, 32, 48, 256 };
        var sheetPath = Path.Combine(Path.GetTempPath(), "claude-manager-icons.png");
        using (var sheet = new Bitmap(sizes.Sum() + 10 * sizes.Length, 260 * specs.Count / 4 + 260))
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.White);
            int y = 0;
            foreach (var spec in specs)
            {
                int x = 0;
                foreach (var s in sizes)
                {
                    using var b = IconFactory.RenderSpec(spec, IconFactory.Parse("#2F80ED"), "W", s);
                    if (y + s <= sheet.Height) g.DrawImage(b, x, y);
                    x += s + 10;
                }
                y += 58; // 256 px column overlaps rows; small sizes are what matters in menus
            }
            sheet.Save(sheetPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        o.WriteLine($"Icon preview sheet: {sheetPath} (Claude exe: {IconFactory.ClaudeExe ?? "not found, drawn fallback"})");
        for (int i = 0; i < 3; i++) foreach (var spec in specs) IconFactory.RenderSpec(spec, Color.Teal, "W", 32).Dispose();
        var i0 = Take();
        for (int i = 0; i < 200; i++) foreach (var spec in specs) foreach (var s in sizes) IconFactory.RenderSpec(spec, Color.Teal, "W", s).Dispose();
        var i1 = Take();
        Print(o, $"icon render x{200 * specs.Count * sizes.Length}", i0, i1);
        bool okIcons = i1.Gdi - i0.Gdi < 3 && i1.Handles - i0.Handles < 20 && i1.Private - i0.Private < 30_000_000;

        var pass = okLookup && okSig && okMenu && okIcons;
        o.WriteLine(pass ? "NO LEAKS DETECTED" : "POSSIBLE LEAK: see growth above");
        return pass ? 0 : 1;
    }
}
