# OpenCode Go Desktop Widget

**English** | [中文](README.zh-CN.md)

A tiny Windows desktop floating widget that shows your **OpenCode Go** quota — the 5-hour, weekly and monthly usage windows — in real time, right on your screen.

![Widget](docs/screenshot.png)
![Mini pill mode](docs/screenshot-mini.png)

> ~10 MB memory · 0% idle CPU · native C# (no Electron, no WebView, no background services)

## Features

- **Live quota meters** — 5-hour, weekly and monthly windows with exact USD amounts (`$2.69/$12`), percentage bars and reset countdowns.
- **Runs together with OpenCode** — the widget opens automatically when OpenCode Desktop starts and exits when it closes.
- **Feels native** — drag it anywhere, resize it freely, tune opacity (25%–100%), pin it on top.
- **Mini pill mode** — double-click to collapse into a one-line pill: `Go | 5h:22% · Wk:66% · Mo:83%`.
- **Remaining / used toggle** — one click to flip between “how much is left” and “how much was used”.
- **Manual + automatic refresh** — hit `↻ Refresh` (or `F5`) any time; the widget also refreshes quietly after every AI reply and every 2 minutes.
- **Bilingual UI** — follows your Windows display language (English / 中文), or force it via `widget-settings.json`.

## Requirements

- Windows 10 / 11
- [OpenCode](https://opencode.ai) **Desktop** (V2), signed in to OpenCode Console with an **OpenCode Go** or **Go Plus** subscription
- Nothing else. The widget builds with the .NET Framework 4.x compiler that ships with Windows.

## Install

1. **Clone this repository** anywhere you like (it stays a normal folder; nothing is installed system-wide):

   ```powershell
   git clone https://github.com/eittyneo/opencode-go-widget.git
   cd opencode-go-widget
   ```

2. **Build the widget** (takes about a second):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```

3. **Install the OpenCode plugin** (writes a small loader file into your OpenCode config folder):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1
   ```

4. **Restart OpenCode.** The widget appears — from then on it starts and exits together with OpenCode.

If you close the widget while OpenCode is running, type `/widget` inside OpenCode (or double-click `launch-widget.bat`) to bring it back.

## Usage

| Action | How |
| --- | --- |
| Move | Drag anywhere on the card |
| Resize | Drag any edge or the striped corner grip |
| Refresh now | `↻ Refresh` button · `F5` · right-click menu |
| Remaining / used | `Remaining` / `Used` button · right-click menu |
| Opacity | `-` / `+` buttons · `Ctrl + mouse wheel` · right-click menu |
| Mini pill mode | Double-click the card · `▴` / `▾` button |
| Always on top | `Top` button (highlighted = on) · right-click menu |
| Close | Right-click → *Hide & close widget* (reopen with `/widget`) |

Inside OpenCode:

- `/widget` — reopen the floating widget.

## How it works

```text
 ┌─────────────────────────────┐        ┌──────────────────────────────────────┐
 │      OpenCode Desktop       │ starts │  plugin.js  (this repo, runs inside   │
 │        (OpenCode.exe)       ├───────►│  the OpenCode server)                │
 └──────────────┬──────────────┘        │                                      │
                │ quits                 │  reads your OpenCode sign-in through │
                ▼                       │  OpenCode's own integration API      │
 ┌─────────────────────────────┐        │               │                      │
 │  OpenCodeQuotaWidget.exe    │        │               ▼                      │
 │  • local HTTP client  ◄─────┼────────┤  GET https://opencode.ai/console/    │
 │  • exits when OpenCode exits│  HTTP  │      api/go/status                   │
 └─────────────────────────────┘ :19876 │  (your own token, refreshed by       │
        (native C#, ~10 MB)              │   OpenCode when it expires)          │
                                         └──────────────────────────────────────┘
```

- The plugin (`plugin.js`) loads inside the OpenCode server, reads your Console sign-in through OpenCode’s integration API and calls the **same usage endpoint the official console page uses**, then serves a tiny JSON on `http://127.0.0.1:19876/quota` and caches it to `quota-live.json`.
- The widget (`OpenCodeQuotaWidget.exe`) polls that local endpoint, so refreshing is instant and cheap. If OpenCode is not running, it falls back to `fetch-quota.mjs` (Node ≥ 22.5) and then to the cached JSON.
- **This is fully standalone.** In particular it does **not** read data from `@slkiser/opencode-quota` — that’s a great plugin for in-TUI quota panels, and both can run side by side, but they are unrelated code paths.
- `/api/go/status` is the endpoint the OpenCode console itself uses; it is not documented as a public API, so it may change. If it does, the widget degrades gracefully (shows an error and keeps the last cached values).

## Files

| File | Purpose |
| --- | --- |
| `OpenCodeQuotaWidget.cs` | The widget itself — native C# / WinForms, GDI+ rendered |
| `OpenCodeQuotaWidget.exe` | Build output (created by `build.ps1`, not committed) |
| `plugin.js` | OpenCode plugin: quota fetch, local HTTP endpoint, widget lifecycle, `/widget` command |
| `fetch-quota.mjs` | Standalone fallback fetcher (needs Node ≥ 22.5) |
| `build.ps1` | Builds the exe with the built-in .NET Framework compiler |
| `install.ps1` | Builds + writes the plugin loader into `~/.config/opencode/plugins/` |
| `launch-widget.bat` | Reopens the widget manually |
| `quota-live.json` | Runtime cache (created at runtime, not committed) |
| `widget-settings.json` | Your widget preferences (created at runtime, not committed) |

## Configuration

The widget keeps its state in `widget-settings.json` next to the exe:

```jsonc
{
  "X": 2238, "Y": 1183,          // saved position
  "Width": 320, "Height": 220,   // saved size
  "Opacity": 0.75,               // 0.25 – 1.0
  "TopMost": true,               // always on top
  "ShowRemaining": true,         // start in "remaining" mode
  "MiniMode": false,             // start collapsed
  "SavedFullHeight": 220,
  "Language": "auto"             // "auto" | "en" | "zh"
}
```

You normally never need to edit it — the widget writes it for you. Delete the file to reset everything.

## Uninstall

1. Delete the loader file `%USERPROFILE%\.config\opencode\plugins\opencode-go-widget.js` (or wherever `install.ps1` told you it wrote it).
2. Delete this repository folder.
3. Done — nothing else was installed.

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Widget doesn’t appear | Make sure OpenCode Desktop is running and you ran `install.ps1`. Type `/widget` inside OpenCode. You can also start `OpenCodeQuotaWidget.exe` directly to see what happens. |
| Shows an error or `Console API error` | Sign out and back in inside OpenCode, then hit `↻ Refresh`. |
| Values look stale | Click `↻ Refresh`. If OpenCode has been closed for a long time its token may have expired — open OpenCode once so it refreshes the token. |
| Windows port 19876 busy | Another instance is already running — only one widget instance is allowed (single-instance mutex). |

## Privacy

- Your OpenCode access token stays on your machine. It is read from OpenCode’s own credential store and is only sent to `opencode.ai` (the same endpoints OpenCode itself calls).
- No telemetry, no analytics, no third-party servers. The only files written are `quota-live.json` and `widget-settings.json` next to the exe.

## License

[MIT](LICENSE). Not affiliated with OpenCode.
