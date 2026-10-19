using System.Reflection;

namespace ClaudeManager.Core;

/// <summary>Version and the "not endorsed by Anthropic" notice, shown in the app, README and installer.</summary>
public static class About
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public const string ShortDisclaimer =
        "Unofficial community tool. Not affiliated with, endorsed by, or supported by Anthropic.";

    public const string Disclaimer =
        "Claude Manager is an unofficial, free utility made by the community to help people who use more than one " +
        "Claude account. It is not affiliated with, endorsed by, sponsored by, or supported by Anthropic.\n\n" +
        "\"Claude\" and the Claude logo are trademarks of Anthropic, PBC, and are used here only to describe which app this tool works with. " +
        "The Claude icon shown for profiles is read from your own Claude installation; it is not included in Claude Manager.\n\n" +
        "Claude Manager only chooses which folder each Claude window uses. It never reads, copies or sends your logins, tokens or chats, " +
        "and it does not modify Claude. Use it with your own accounts and follow Anthropic's terms and your organisation's rules.";
}
