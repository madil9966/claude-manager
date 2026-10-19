namespace ClaudeManager.UI;

internal static class MenuUtil
{
    /// <summary>
    /// Items.Clear() does not dispose items, so menus rebuilt on every open would leak.
    /// Images come from a shared cache, so they are detached (not disposed) first.
    /// </summary>
    public static void Clear(ToolStripItemCollection items)
    {
        var old = items.Cast<ToolStripItem>().ToList();
        items.Clear();
        foreach (var i in old)
        {
            if (i is ToolStripMenuItem mi) Clear(mi.DropDownItems);
            i.Image = null;
            i.Font = null!; // shared bold font: don't let the item own it
            i.Dispose();
        }
    }
}
