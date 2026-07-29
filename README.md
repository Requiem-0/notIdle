# NotIdle

A lightweight Windows tray utility that watches for user inactivity and, after a
configurable idle threshold, performs a brief mouse "keep-awake" nudge for a
configurable duration. No network, no accounts, no telemetry — it does nothing
observable beyond a tray icon and a settings file.

## Design at a glance

- **`NotIdle.Core`** — the background engine. UI-free and unit-testable. A single
  self-rescheduling timer polls the near-free `GetLastInputInfo` syscall (no input
  hooks → zero CPU while you're active) and drives a small state machine
  (`Paused → Monitoring → Idle → ActionRunning`).
- **`NotIdle.App`** — the WinForms tray shell: `NotifyIcon`, context menu, and an
  on-demand settings dialog (created when opened, disposed on close). Subscribes to
  the engine and marshals its callbacks onto the UI thread. Holds no business logic.
- **Communication** — plain in-process C# events. No IPC, no second process, no
  Windows Service.
- **Config** — one JSON file at `%APPDATA%\NotIdle\settings.json`, written atomically
  (temp + move) and loaded defensively (corrupt file → defaults).

```
src/
├─ NotIdle.Core/            # engine, no UI
│  ├─ IdleEngine.cs         # timer + state machine (allocation-free hot path)
│  ├─ AppState.cs  Settings.cs  Abstractions.cs
│  ├─ JsonSettingsStore.cs  # %APPDATA%\NotIdle\settings.json (source-gen JSON)
│  └─ Native/               # GetLastInputInfo, SendInput (P/Invoke)
└─ NotIdle.App/             # WinForms tray
   ├─ Program.cs            # entry, single-instance mutex, composition root
   ├─ TrayApplicationContext.cs   # tray icon + menu
   ├─ ControlPanelForm.cs   # the one settings/status window (start/stop/apply/test)
   ├─ TrayIconFactory.cs    MemoryOptimizer.cs
   └─ Startup/StartupManager.cs   # HKCU Run-key toggle
```

The idle threshold accepts a value plus a **seconds / minutes** unit, so you can
set anything from a few seconds (handy for testing) up to hours.

## Install as a standalone app (recommended)

Do **not** run it long-term with `dotnet run` — that ties the process to the
terminal/VS Code, so closing them kills it (and holds a lock on the DLLs).
Instead install it once:

```powershell
.\install.ps1              # build + install to %LOCALAPPDATA%\NotIdle + launch
.\install.ps1 -NoLaunch    # build + install only
.\install.ps1 -Uninstall   # stop + remove
```

This publishes a self-contained single-file exe (the .NET runtime is bundled —
the machine needs nothing installed), copies it to `%LOCALAPPDATA%\NotIdle`,
adds a Start Menu shortcut, and launches it. After that **you can close VS Code
entirely** — NotIdle runs as its own process. Use the app's *Start with Windows*
toggle for auto-start on sign-in.

Installed exe: ~52 MB on disk; **~16–22 MB working set** after the startup trim;
~0% idle CPU.

## Dev inner loop

```powershell
dotnet build NotIdle.sln -c Debug
dotnet run --project src/NotIdle.App    # convenient while coding; Ctrl-C / close to stop
```

> **Note on NativeAOT:** WinForms does **not** support trimming/AOT (SDK error
> NETSDK1175). The self-contained single-file build above is the lightest supported
> WinForms option. To reach true AOT (~sub-15 MB exe, lower private memory), the tray
> shell must be rewritten against Win32 `Shell_NotifyIcon` directly — `NotIdle.Core`
> stays unchanged. See the roadmap's "Production" stage.

## Resource behaviour

- **CPU:** one cheap syscall ~once/second while monitoring; timer *stops entirely*
  when paused/disabled (literally 0% CPU). Brisk 100 ms ticks only during the short
  action window.
- **RAM:** settings dialog is not resident while idle; working set trimmed once after
  startup (`SetProcessWorkingSetSize`).
- **Battery:** low-frequency coalescible polling instead of per-input hooks.

## Next steps (see roadmap in project notes)

- Code-sign the exe (avoids SmartScreen/AV friction for a mouse-moving tool).
- Installer via Inno Setup or WiX (adds Add/Remove Programs entry + clean uninstall).
- Handle session lock/unlock and power suspend/resume.
- Optional: Win32-`Shell_NotifyIcon` shell for a true-AOT, minimal-footprint build.
