# Contributing

Thanks for taking a look at CodexBarWin.

This is a small Windows-only WinUI 3 tray app. Keep changes focused, practical, and easy to review.

## Build

From the repository root:

```powershell
dotnet build CodexBarWin.csproj -c Release
```

## Development Notes

- Windows 11 is the expected development target.
- Keep the app standalone; it should not depend on any separate local monitor project.
- The tray icon uses native `Shell_NotifyIcon` interop in `NativeTrayIcon.cs`.
- Keep `Microsoft.WindowsAppSDK` pinned unless there is a deliberate version-change discussion.
- Do not commit generated `bin/`, `obj/`, release artifacts, local app data, or secrets.
- Do not paste Codex auth files, OAuth tokens, account ids, or private logs into issues or pull requests.

## Pull Requests

For code changes, please describe:

- What changed.
- How it was tested.
- Any user-visible behavior changes.

For UI changes, screenshots are useful.
