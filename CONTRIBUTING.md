# Contributing to TokenTray

Thanks for helping. The most useful things right now:

- **Reports from setups we can't test:** Cursor, OpenCode Go, Grok monthly plans, Codex plans with a 5-hour window, Windows 10, ARM64, multi-monitor and left-aligned taskbars.
- **A usage response that TokenTray reads wrong.** Include the JSON shape with tokens, emails and ids removed.

## Development

```powershell
dotnet test                          # core logic, no network
dotnet run --project src/TokenTray   # the app
```

- `src/TokenTray.Core` — providers, polling, pace and verdicts, cache. No UI; everything here should have a test.
- `src/TokenTray` — WPF: taskbar widget (a layered child window of the taskbar), panel, hover card, tray icon, settings.

Rules that keep TokenTray trustworthy:

1. **Credentials are read-only.** Never refresh a token or write to another tool's files.
2. **Only official endpoints**, only the ones each tool itself uses to show usage.
3. **Never mix accounts.** A reading belongs to one account id; old numbers are never shown for a different one.
4. **Respect rate limits.** No request before a `Retry-After` ends.
5. **Nothing personal in logs, screenshots or tests.** Use `--render-media` for images; it draws sample data.

Keep changes focused, match the surrounding style, and run `dotnet test` before opening a pull request.
