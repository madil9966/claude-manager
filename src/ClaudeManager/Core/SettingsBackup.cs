using System.IO.Compression;
using System.Text.Json.Nodes;

namespace ClaudeManager.Core;

public sealed record BackupResult(string ZipPath, int Files, List<string> Skipped, int SecretsRemoved = 0);

/// <summary>Replaces secret values in a JSON tree (in place). Returns how many values were replaced.</summary>
public static class SecretScrubber
{
    public const string Placeholder = "<removed by Claude Manager>";

    /// <summary>Objects whose every value is treated as secret (environment variables, HTTP headers).</summary>
    static readonly string[] SecretContainers = { "env", "headers", "environment" };

    static readonly System.Text.RegularExpressions.Regex SecretName = new(
        @"(api[_-]?key|apikey|token|secret|password|passwd|credential|authorization|auth[_-]?header|bearer|private[_-]?key|client[_-]?secret|cookie|session[_-]?key)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Key names that look secret but only hold numbers/flags (e.g. token counts).</summary>
    static readonly System.Text.RegularExpressions.Regex HarmlessName = new(
        @"(counts?|limits?|max|min|usages?|budgets?|expires|expiry|ttl|lengths?|enabled|disabled|tokens?_?used|tokenizer|at)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    public static int Scrub(JsonNode? node) => node switch
    {
        JsonObject o => ScrubObject(o),
        JsonArray a => a.Sum(Scrub),
        _ => 0,
    };

    static int ScrubObject(JsonObject o)
    {
        int n = 0;
        foreach (var key in o.Select(kv => kv.Key).ToList())
        {
            var value = o[key];
            if (value is JsonObject inner && SecretContainers.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var k in inner.Select(kv => kv.Key).ToList())
                    if (inner[k] is JsonValue) { inner[k] = Placeholder; n++; }
                    else n += Scrub(inner[k]);
            }
            else if (value is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                     && SecretName.IsMatch(key) && !HarmlessName.IsMatch(key))
            {
                o[key] = Placeholder;
                n++;
            }
            else if (value is JsonObject or JsonArray && SecretName.IsMatch(key) && !HarmlessName.IsMatch(key))
            {
                // e.g. customApiKeyResponses: lists of key fragments. Drop the whole value.
                o[key] = Placeholder;
                n++;
            }
            else if (value is JsonArray arr && key.Equals("args", StringComparison.OrdinalIgnoreCase))
            {
                // MCP servers often get keys on the command line: "--api-key", "sk-..." or "--token=...".
                n += ScrubArgs(arr);
            }
            else n += Scrub(value);
        }
        return n;
    }

    static int ScrubArgs(JsonArray args)
    {
        int n = 0;
        bool nextIsSecret = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] is not JsonValue v || !v.TryGetValue<string>(out var s)) { nextIsSecret = false; continue; }
            if (nextIsSecret) { args[i] = Placeholder; n++; nextIsSecret = false; continue; }
            var eq = s.IndexOf('=');
            if (s.StartsWith('-') && SecretName.IsMatch(eq > 0 ? s[..eq] : s))
            {
                if (eq > 0) { args[i] = s[..(eq + 1)] + Placeholder; n++; }
                else nextIsSecret = true;
            }
            else if (LooksLikeKey(s)) { args[i] = Placeholder; n++; }
        }
        return n;
    }

    /// <summary>Well-known key formats (Anthropic, OpenAI, GitHub, Slack, AWS, Google, bearer tokens).</summary>
    static bool LooksLikeKey(string s) =>
        System.Text.RegularExpressions.Regex.IsMatch(s,
            @"^(sk-[A-Za-z0-9_\-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|xox[abprs]-[A-Za-z0-9\-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_\-]{30,}|Bearer\s+\S{10,})$");
}

/// <summary>
/// Read-only backup of the existing setup's settings into a zip under %USERPROFILE%\ClaudeManager-Backups.
/// Login tokens are never copied: .credentials.json is skipped and primaryApiKey is stripped from .claude.json.
/// Nothing is ever written back into the Claude folders (restore is manual, see README.txt in the zip).
/// </summary>
public static class SettingsBackup
{
    static readonly string[] CliItems =
    {
        "settings.json", "settings.local.json", "CLAUDE.md", "keybindings.json",
        "agents", "commands", "skills", "hooks", "output-styles", "rules",
        @"plugins\installed_plugins.json", @"plugins\known_marketplaces.json", @"plugins\config.json",
        "history.jsonl",
    };

    static readonly string[] SecretFiles = { ".credentials.json" };
    const long MaxFileSize = 50 * 1024 * 1024;

    [ThreadStatic] static int Redacted;

    public static BackupResult Create()
    {
        Redacted = 0;
        Directory.CreateDirectory(Paths.BackupDir);
        var zipPath = Path.Combine(Paths.BackupDir, $"claude-settings-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        SafetyGuard.AssertWritable(zipPath);
        int files = 0;
        var skipped = new List<string>();

        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var cliDir = Paths.ExistingCliDir;
            foreach (var item in CliItems)
            {
                var src = Path.Combine(cliDir, item);
                if (File.Exists(src)) AddFile(zip, src, ".claude/" + item.Replace('\\', '/'), ref files, skipped);
                else if (Directory.Exists(src)) AddDir(zip, src, ".claude/" + item, ref files, skipped);
            }

            if (File.Exists(Paths.HomeClaudeJson))
                AddFile(zip, Paths.HomeClaudeJson, ".claude.json", ref files, skipped);

            var desktopCfg = Path.Combine(Paths.DefaultDesktopDir, "claude_desktop_config.json");
            if (File.Exists(desktopCfg)) AddFile(zip, desktopCfg, "AppData-Claude/claude_desktop_config.json", ref files, skipped);

            var readme = zip.CreateEntry("README.txt");
            using (var w = new StreamWriter(readme.Open()))
            {
                w.WriteLine($"Claude settings backup made by Claude Manager on {DateTime.Now:yyyy-MM-dd HH:mm}.");
                w.WriteLine($".claude/                -> {cliDir}");
                w.WriteLine($".claude.json            -> {Paths.HomeClaudeJson}");
                w.WriteLine($"AppData-Claude/         -> {Paths.DefaultDesktopDir}");
                w.WriteLine("Login tokens are NOT included; sign in again after a restore.");
                w.WriteLine($"Secret values in JSON files (API keys, tokens, MCP server env/headers) were replaced with \"{SecretScrubber.Placeholder}\" ({Redacted} values).");
                w.WriteLine("Re-enter those values by hand after a restore. history.jsonl is copied as-is and may contain anything you typed.");
                w.WriteLine("To restore, close Claude and copy the files back by hand.");
            }
        }

        Log.Info($"Settings backup created: {zipPath} ({files} files, {skipped.Count} skipped, {Redacted} secret values removed)");
        return new BackupResult(zipPath, files, skipped, Redacted);
    }

    static void AddDir(ZipArchive zip, string dir, string prefix, ref int files, List<string> skipped)
    {
        var root = new DirectoryInfo(dir);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped.Add(dir + " (link)"); return; }
        foreach (var e in root.EnumerateFileSystemInfos())
        {
            if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { skipped.Add(e.FullName + " (link)"); continue; }
            if (e is DirectoryInfo d) AddDir(zip, d.FullName, prefix + "/" + d.Name, ref files, skipped);
            else AddFile(zip, e.FullName, prefix.Replace('\\', '/') + "/" + e.Name, ref files, skipped);
        }
    }

    static void AddFile(ZipArchive zip, string src, string entryName, ref int files, List<string> skipped)
    {
        if (SecretFiles.Contains(Path.GetFileName(src), StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var fi = new FileInfo(src);
            if (fi.Length > MaxFileSize) { skipped.Add($"{src} (larger than 50 MB)"); return; }

            // JSON settings can hold API keys (MCP server "env"/"headers", settings.json "env", primaryApiKey...).
            // They are copied with every secret value replaced. A JSON file that can't be parsed can't be
            // cleaned, so it is skipped rather than copied as-is.
            if (src.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                JsonNode? node;
                try { node = JsonNode.Parse(ReadShared(src)); }
                catch (Exception ex) { skipped.Add($"{src} (not valid JSON, so secrets couldn't be removed: {ex.Message})"); return; }
                var removed = SecretScrubber.Scrub(node);
                var jentry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                jentry.LastWriteTime = fi.LastWriteTime;
                using (var w = new StreamWriter(jentry.Open()))
                    w.Write(node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "null");
                files++;
                if (removed > 0) Redacted += removed;
                return;
            }
            using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            entry.LastWriteTime = fi.LastWriteTime;
            using var output = entry.Open();
            input.CopyTo(output);
            files++;
        }
        catch (Exception ex) { skipped.Add($"{src} ({ex.Message})"); }
    }

    static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }
}
