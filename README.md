# NotIdle

A lightweight Windows tray utility that keeps your computer and session awake when
you deliberately step away — while keeping an honest, separate record of when you
were genuinely at the keyboard. No network, no accounts, no telemetry.

## What it actually does

Keeping a session awake needs two different things, and NotIdle uses the smallest
mechanism for each:

1. **`SetThreadExecutionState`** — the supported Windows API for "don't sleep while
   I'm doing this", the same one media players and installers use. Held for as long
   as keep-awake is on; prevents system sleep and, optionally, display-off. This is
   the primary mechanism and involves no synthetic input at all.
2. **A 1-pixel cursor move, out and straight back**, on a randomized interval. The
   power request above doesn't touch the *input* idle clock that session-idle and
   lock-on-idle policies read; one imperceptible movement resets it.

That is the entire synthetic-input surface. NotIdle **never** clicks, types,
scrolls, or interacts with any window or control, and makes no attempt to imitate a
person working.

## Genuine activity vs. keep-awake

`GetLastInputInfo` is reset by *injected* input as well as real input, so the raw
counter cannot tell the two apart. NotIdle keeps its own genuine-idle clock:
`KeepAwakeEngine` reports the tick at which it pulsed, and `UserActivityMonitor`
discounts an input event only when **both** the timestamp lands inside a 400 ms
window of that pulse **and** the cursor is exactly where the pulse left it.
Everything else counts as real. Ambiguity resolves toward "genuine", so real input
is far likelier to be over-counted than missed.

The upshot: leave the machine for ten minutes and *Since genuine interaction* keeps
climbing past ten minutes, however many keep-awake pulses fired in between.

## States

| State | Meaning |
|---|---|
| `Active` | Genuine interaction seen in the last few seconds |
| `Idle` | No interaction, threshold not yet reached |
| `KeepAwake` | Threshold passed — holding the session awake, cursor pulses running |
| `Suspended` | A full-screen, game, or presentation app has focus: power request held, cursor pulses stopped |
| `Locked` | Workstation locked: power request held, cursor pulses stopped |
| `Disabled` | Switched off; normal power policy applies |

Every enabled state holds the power request — including `Suspended` and `Locked`.
A full-screen film is exactly when the display should stay on, and a locked machine
should still finish its downloads. What varies between states is whether a **cursor
pulse** is warranted, and only `KeepAwake` qualifies.

Lock/unlock and sleep/resume are the two moments polling cannot infer on its own:
no input arrives while locked or asleep, yet a person is unmistakably present the
instant either ends. `SystemEvents.SessionSwitch` and `PowerModeChanged` feed those
into the controller, which re-baselines the genuine-activity clock on unlock and on
resume.

Context awareness comes from `SHQueryUserNotificationState` (the shell's own
"is now a bad time?" answer, covering presentation mode and exclusive-fullscreen
D3D) plus a geometry check that catches borderless-fullscreen apps.

## Design at a glance

- **`NotIdle.Core`** — UI-free and unit-testable. One self-rescheduling 1 s timer in
  `NotIdleController` drives everything; detection is low-frequency polling of
  near-free syscalls rather than input hooks, so the CPU stays parked while you work.
- **`NotIdle.App`** — the WinForms tray shell plus a dashboard window whose refresh
  timer only runs while it is visible. Holds no business logic.
- **Communication** — plain in-process C# events. No IPC, no second process, no service.
- **Config** — one JSON file at `%APPDATA%\NotIdle\settings.json`, written atomically
  (temp + move) and loaded defensively (corrupt file → defaults).

```
src/
+- NotIdle.Core/                  # engine, no UI
|  +- NotIdleController.cs        # the one timer; observe -> decide -> act
|  +- Monitoring/
|  |  +- UserActivityMonitor.cs   # genuine input, with synthetic attribution
|  |  +- WindowMonitor.cs         # foreground app / full-screen state
|  +- IdleStateManager.cs         # the state machine (pure logic, no I/O)
|  +- KeepAwakeEngine.cs          # wake request + randomized 1 px pulse
|  +- SettingsManager.cs          # loads, normalizes, persists, notifies
|  +- ActivityState.cs  Settings.cs  NotIdleStatus.cs  Abstractions.cs
|  +- JsonSettingsStore.cs        # %APPDATA%\NotIdle\settings.json (source-gen JSON)
|  +- Native/                     # GetLastInputInfo, SendInput, SetThreadExecutionState,
|                                 #   foreground-window + notification-state probes
+- NotIdle.App/                   # WinForms tray
   +- Program.cs                  # entry, single-instance mutex, composition root
   +- TrayApplicationContext.cs   # tray icon + menu, SystemEvents wiring
   +- DashboardForm.cs            # dashboard + settings, one window
   +- Theme.cs  TrayIconFactory.cs  MemoryOptimizer.cs
   +- Startup/StartupManager.cs   # HKCU Run-key toggle

tests/
+- NotIdle.Core.Tests/            # xUnit; virtual clock, no real timers
```

## Settings

| Setting | Default | Notes |
|---|---|---|
| Enable keep-awake | on | Master toggle; off means zero timers |
| Idle before keep-awake | 5 min | Accepts seconds or minutes |
| Minimum / maximum interval | 45 s / 120 s | Each pulse waits a fresh uniform draw from this range, plus sub-second jitter |
| Return the cursor | on | Explicit restore, so a pulse racing your hand can't displace the pointer |
| Keep the display on | on | Adds `ES_DISPLAY_REQUIRED` |
| Pause for full-screen apps | on | Games, presentations, borderless-fullscreen |
| Auto-off after | 0 (never) | Switches keep-awake off N minutes after it is turned on |
| Start with Windows | off | Per-user `HKCU\…\Run`; no admin rights |

Note on the wake request: it is held from the moment keep-awake is enabled, not only
once the threshold expires. Otherwise a threshold longer than the machine's own sleep
timer would never get the chance to fire. No synthetic input is emitted while you're
active — only the power request is held.

Auto-off exists because the real failure mode of these tools is leaving one running
for weeks without noticing. The countdown starts when keep-awake is switched on and
is *not* extended by activity; saving an unrelated setting does not restart it.

## Tests

```powershell
dotnet test tests/NotIdle.Core.Tests
```

48 tests over the pure core. The load-bearing ones assert the invariant the whole
app rests on: `FakeDesktop` resets its input timestamp on synthetic pulses exactly
as `GetLastInputInfo` does, then the suite proves ten simulated minutes of pulsing
leaks zero genuine events and never stalls the genuine-idle clock.

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

- **CPU:** one small batch of cheap syscalls once per second; the timer *stops
  entirely* when disabled (literally 0% CPU). Measured ~0.03 s CPU over 12 s of
  runtime, nearly all of it startup.
- **RAM:** ~17 MB working set; the dashboard's refresh timer only runs while the
  window is visible, and the working set is trimmed once after startup
  (`SetProcessWorkingSetSize`).
- **Battery:** low-frequency coalescible polling instead of per-input hooks.

## Next steps (see roadmap in project notes)

- Code-sign the exe (avoids SmartScreen/AV friction for an input-injecting tool).
- Installer via Inno Setup or WiX (adds Add/Remove Programs entry + clean uninstall).
- Optional: Win32-`Shell_NotifyIcon` shell for a true-AOT, minimal-footprint build.
