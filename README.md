# Claude Manager

> **Unofficial community tool.** Claude Manager is a free utility made by the community to help people who use more than one Claude account. It is **not affiliated with, endorsed by, sponsored by, or supported by Anthropic**. "Claude" and the Claude logo are trademarks of Anthropic, PBC, used here only to describe the app this tool works with. No Anthropic software or assets are included.

A small Windows tray app that runs several Claude accounts side by side (for example Work and Personal), for both the **Claude desktop app** and the **Claude Code CLI**, without logging out and in.

It is a thin launcher. It only decides which folder Claude uses. It never reads, copies or sends logins, tokens or chats, and it never modifies or patches Claude. No telemetry.

## Install

1. Download `ClaudeManager-Setup-<version>.exe` from [Releases](https://github.com/madil9966/claude-manager/releases).
2. Run it. It installs for your Windows user only (no administrator rights) into `%LOCALAPPDATA%\Programs\Claude Manager`, and adds Start-menu and Desktop shortcuts. "Start with Windows" is optional.
3. Windows may show "Windows protected your PC" because the installer isn't code-signed yet. Click *More info*, then *Run anyway*. Check the file against `SHA256SUMS.txt` from the release if you want to be sure.

Requirements: Windows 10 or 11 (x64), the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (Windows offers to install it if it's missing), and the Claude desktop app and/or Claude Code.

**Portable:** unzip `ClaudeManager-<version>-portable.zip` anywhere and run `ClaudeManager.exe`. If you move it later, it repoints its shortcuts and startup entry the next time it starts.

**Uninstall:** Settings → Apps → Installed apps → *Claude Manager (unofficial)* → Uninstall. You can also use Claude Manager → Settings ▾ → About → *Uninstall…*, or run `ClaudeManager.exe --uninstall` for the portable version. This removes shortcuts, the startup entry and the `claude://` link routing, so links go straight to Claude again. You can optionally remove Claude Manager's own settings too. Your Claude accounts, profile folders, logins and chats are never deleted.

**Silent install** (for scripts): `ClaudeManager-Setup-<version>.exe /S [--dir="C:\path"] [--no-desktop] [--no-startmenu] [--no-startup] [--launch]`. Silent uninstall: `ClaudeManager.exe --uninstall --quiet`.

## Your current setup is protected

| Existing data | What Claude Manager does with it |
|---|---|
| `%USERPROFILE%\.claude` (or a `CLAUDE_CONFIG_DIR` you set yourself) | Never writes, moves or deletes |
| `%USERPROFILE%\.claude.json` | Never writes, moves or deletes |
| `%APPDATA%\Claude` | Never writes, moves or deletes |

How that is enforced:

- **Default profile.** Your current setup appears as a profile (named "Default" unless you rename it). It opens Claude exactly as today: the desktop app starts with no `--user-data-dir` flag, and Claude Code starts with your normal config. Its folders can't be changed, and the profile can't be deleted.
- **Safety gate.** Every write and delete goes through one check (`Core/SafetyGuard.cs`). A profile folder can't be inside, equal to or a parent of the protected paths. The check also runs on where the path really leads: junctions or symlinks anywhere in the path, 8.3 short names and `subst` drives are resolved by Windows first. Network and device paths (`\\server\…`, `\\?\…`) are refused. Deletes never follow links and only remove folders Claude Manager created itself.
- **No nested profiles.** A profile's folders can't be the same as, inside, or contain another profile's folders, so deleting one profile can never remove another's data.
- **Sign-in links go to the right profile.** Windows sends every `claude://` link (including the Google sign-in callback) to one registered command, which is Claude itself with no profile folder, so links would always open the Default profile. Claude Manager points that one registry value (`HKCU\Software\Classes\claude\shell\open\command`) at itself and forwards each link to the right profile: the profile that's waiting for its first sign-in, otherwise the Claude window you used last, otherwise you choose. Claude registers itself again each time it starts, so the value is watched and re-applied. Claude's original command is saved, and Settings → *Send claude:// links to the right profile* (turn it off) writes it back. Uninstalling hands links back to Claude automatically. If you delete a portable copy by hand, turn this off first. Links are never logged with their sign-in codes. A pending first sign-in is remembered for 30 minutes, even across a restart of Claude Manager.
- **Old Claude Code is blocked.** Versions older than 2.1.140 read and write `~\.claude.json` even when a profile folder is set. Profile terminals stay blocked until you run `claude update`. If the version can't be read at all (timeout, unexpected output), profile terminals are blocked too until it can be confirmed.
- **Always the current Claude.** Claude keeps old version folders after it updates itself. Before each launch Claude Manager checks for a newer version, so profiles never open their data with an older Claude than the one that last wrote it.
- **Profile folder check.** During a first desktop sign-in, the app checks that Claude really writes to the profile folder. If it doesn't (for example, an update made Claude ignore the flag), you're told not to sign in, so your current account isn't overwritten. The first Claude Code terminal of a profile gets the same check.
- **Optional backup.** Settings → *Back up current Claude settings*, also offered on first run. It writes a zip to `%USERPROFILE%\ClaudeManager-Backups`, reading files only. The zip holds `settings.json`, `CLAUDE.md`, agents, commands, skills, hooks, history, `.claude.json` and `claude_desktop_config.json`. Login tokens (`.credentials.json`) are never copied. In every JSON file, secret values are replaced with a placeholder: `primaryApiKey`, API keys, tokens, passwords, MCP server `env` and `headers`, and keys passed in MCP `args`. A JSON file that can't be parsed is skipped rather than copied as-is. `history.jsonl` is copied unchanged and may contain anything you typed. To restore, copy files back by hand and re-enter the removed values.

Check it yourself: `ClaudeManager.exe --selftest > result.txt`. It briefly creates a junction in `%TEMP%` and a `subst` drive letter to test path checks, and removes both again.

## Build from source

Requires the .NET 9 SDK.

```
dotnet build ClaudeManager.sln -c Release
src\ClaudeManager\bin\Release\net9.0-windows\ClaudeManager.exe
```

To build the installer, portable zip and checksums into `dist\`:

```
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1             # needs .NET 9 Desktop Runtime on the target PC
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 -SelfContained   # bundles the runtime (bigger, nothing to install)
```

The script publishes the app, runs its self-test, then embeds the app in `ClaudeManager-Setup.exe`. That's a small WinForms wizard (`src/ClaudeManager.Setup`) that installs per-user with no admin rights and registers in Apps & Features. The version is set in `src/Directory.Build.props`. The app icon comes from `tools/make-icon.ps1` (`assets/app.ico`).

Diagnostics: `ClaudeManager.exe --stress` (leak check), plus `tools/soak.ps1`, `tools/mem.ps1` and `tools/windows.ps1`.

## License

MIT, see [LICENSE](LICENSE). Contributions and bug reports are welcome at [Issues](https://github.com/madil9966/claude-manager/issues).

## Use

1. On first run, choose names (Work and Personal by default), where the folders go, and whether to back up first.
2. **Desktop** opens an isolated Claude window. A profile's first launch walks you through sign-in. Other Claude windows can stay open, because Claude Manager sends the sign-in link to the profile being set up. Still sign in to one new profile at a time.
3. **Terminal** opens Windows Terminal (cmd.exe) in a project folder, with `CLAUDE_CONFIG_DIR` set for that tab only. Recent folders are remembered. Type `/status` to confirm the account.
4. **Icons.** Each profile shows the Claude icon by default, with a dot in the profile color so profiles can be told apart. In *Edit* you can pick a preset instead (dot, initial letter, work, home, code, star, heart, lightning, school, globe; drawn in the profile color) or **Choose image…** (`.ico`, `.png`, `.jpg`, `.bmp`, `.gif`, or the icon of an `.exe`/`.dll`). Chosen images are copied into Claude Manager's folder, so the original can be moved or deleted. The icon is used in the panel, the tray menu and Desktop shortcuts.
5. **⋯** opens profile actions: edit, Desktop or Start-menu shortcuts, open folders, delete (asks first and names the folders).

Default folders for a profile named Work are `%APPDATA%\Claude-Work` and `%USERPROFILE%\.claude-work`. You can switch to `%USERPROFILE%\.claude-accounts\work\{desktop,cli}`. Existing folders, such as a `.claude-work` you made by hand, are reused as they are and never deleted.

If `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` is set, you're warned and can clear it for that terminal only.

## Files

- `%APPDATA%\ClaudeManager\profiles.json`: names, colors, icons, folder paths and recent folders. No credentials. The previous version is kept as `profiles.json.bak`. If `profiles.json` is ever damaged, it's restored from that backup automatically, and the damaged file is kept as `profiles.json.bad-<time>`.
- `%APPDATA%\ClaudeManager\icons\`: shortcut icons and imported custom profile images.
- `%APPDATA%\ClaudeManager\logs\claude-manager.log`: local launch log, no telemetry.
- `%APPDATA%\ClaudeManager\launchers\*.cmd`: per-profile terminal scripts.

## Notes

- The desktop isolation relies on `--user-data-dir`, which Anthropic does not document and can change. The CLI side uses the documented `CLAUDE_CONFIG_DIR`.
- Company devices may have rules about installing tools or running a second Claude instance. Check them first.
- Intended for one person's own legitimate accounts. It does not bypass usage limits or share accounts.
- Not in v1: sharing skills or rules between profiles (F14), and a PowerShell terminal option.
