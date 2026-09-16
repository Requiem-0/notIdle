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
2. **Synthetic pointer input**, on a randomized interval. The power request above
   doesn't touch the *input* idle clock that session-idle and lock-on-idle policies
   read; pointer input resets it.

## The three actions

Each action has its own enable flag and its own randomized schedule, so any
combination of them can be in flight at the same time. They are not alternatives and
they do not take turns.

| Action | Default | What it does |
|---|---|---|
| **Cursor pulse** | on | One 1-pixel move, out and straight back. Imperceptible; the cheapest thing that resets the idle clock. |
| **Path movement** | off | Walks the pointer along a shape — circle, square, figure eight, random walk, or a hand-typed list of screen coordinates — animated at a configured speed. |
| **Auto-click** | off | Presses a button on a millisecond-granularity timer of its own, because useful click rates are far faster than the one-second poll. Give it a list of click points and it travels between them at a configured speed, clicking each in turn. |

**Every action loses to a real hand.** Both movers share one rule, implemented once
in `PointerTravel`: before every frame, check the pointer is still where we put it,
and stop if it isn't. A path run aborts mid-stroke; the auto-clicker disarms itself
outright rather than waiting up to a second for the next poll to notice. Only a hand
on the mouse counts — being stood down by the controller is an orderly stop and is
not recorded as an interruption. The clicker also stands down while a path is being
traced, so a click can never land halfway through a stroke and drag something.

### Click points

With no points configured the auto-clicker never moves the pointer at all: it clicks
wherever the pointer is resting. Give it a list and it cycles through them —

```
420,300   980,640   1500,380
```

— travelling to the next point at the configured speed, clicking, waiting the
interval, then moving on and wrapping around at the end. The travel is interpolated
at ~66 fps, so a low speed produces a visible glide rather than a jump, and the
interference check applies to every frame of it.

Keystrokes are still never synthesized, and no action fires until the idle threshold
has passed.

## Adaptive mode

On by default. NotIdle decides which actions to run and how often, instead of asking
you to guess.

The signal it uses is its own work: injected input and real input reset the same OS
counter, which is a nuisance for attribution but perfect for verification. After a
pulse at tick *T*, the system's last-input timestamp should have moved to
approximately *T*. When it hasn't, the input is being swallowed — a remote session, an
input-blocking policy, a driver coalescing 1-pixel moves away — and pulsing harder is
the right answer.

| Rung | Behaviour | Entered when |
|---|---|---|
| `Minimal` | Pulse only, at your slowest configured interval | 12 consecutive actions landed |
| `Standard` | Pulse only, on your configured range | the default |
| `Assertive` | Pulse **and** path movement, on a shortened interval | 2 consecutive actions swallowed |

Escalation is fast (2 failures) and relaxation is slow (12 successes), so one flake
doesn't move it and one lucky pulse doesn't undo an escalation. If even `Assertive`
gets nowhere for six cycles it stops climbing and says so on the dashboard — at that
point the power request is doing all the work.

The ladder never enables clicking. A click lands *inside* whatever application is
under the pointer; that is a decision you make explicitly, not one a heuristic makes
for you. Adaptive mode can also only *add* to what you asked for — anything you
enabled by hand stays enabled.

## Genuine activity vs. keep-awake

`GetLastInputInfo` is reset by *injected* input as well as real input, so the raw
counter cannot tell the two apart. NotIdle keeps its own genuine-idle clock:
`KeepAwakeEngine` reports the tick of every synthetic event it emits — pulse, path
frame, or click — and `UserActivityMonitor` discounts an input event only when
**both** the timestamp lands inside a 400 ms window of one of ours **and** the cursor
is exactly where we left it.
Everything else counts as real. Ambiguity resolves toward "genuine", so real input
is far likelier to be over-counted than missed.

The upshot: leave the machine for ten minutes and *Since genuine interaction* keeps
climbing past ten minutes, however many keep-awake pulses fired in between.

## States

| State | Meaning |
|---|---|
| `Active` | Genuine interaction seen in the last few seconds |
| `Idle` | No interaction, threshold not yet reached |
| `KeepAwake` | Threshold passed — holding the session awake, the enabled actions running |
| `Suspended` | A full-screen, game, or presentation app has focus: power request held, all actions stopped |
| `Locked` | Workstation locked: power request held, all actions stopped |
| `Disabled` | Switched off; normal power policy applies |

Every enabled state holds the power request — including `Suspended` and `Locked`.
A full-screen film is exactly when the display should stay on, and a locked machine
should still finish its downloads. What varies between states is whether **synthetic
input** is warranted, and only `KeepAwake` qualifies. Leaving `KeepAwake` stops any
path run in flight and disarms the click timer immediately.

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
|  +- KeepAwakeEngine.cs          # wake request + the three independent schedules
|  +- Actions/
|  |  +- CursorPath.cs            # waypoint geometry + coordinate parsing (pure)
|  |  +- PointerTravel.cs         # interpolated movement + the "user always wins" check
|  |  +- PathPlayer.cs            # animates a run off-thread; aborts if you take the mouse
|  |  +- AutoClicker.cs           # own ms-granularity timer; travels to each click point
|  |  +- AdaptiveTuner.cs         # the escalate/relax ladder
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

The **Actions** tab configures the three actions; the **Settings** tab holds
everything global. One Save button writes both.

| Setting | Default | Notes |
|---|---|---|
| Enable keep-awake | on | Master toggle; off means zero timers |
| Idle before keep-awake | 5 min | Accepts seconds or minutes |
| Adaptive mode | on | NotIdle picks the actions and cadence itself; never enables clicking |
| Keep the display on | on | Adds `ES_DISPLAY_REQUIRED` |
| Pause for full-screen apps | on | Games, presentations, borderless-fullscreen |
| Auto-off after | 0 (never) | Switches keep-awake off N minutes after it is turned on |
| Start with Windows | off | Per-user `HKCU\…\Run`; no admin rights |

**Cursor pulse**

| Setting | Default | Notes |
|---|---|---|
| Enabled | on | |
| Minimum / maximum interval | 45 s / 120 s | Each pulse waits a fresh uniform draw from this range, plus sub-second jitter |
| Return the cursor | on | Explicit restore, so a pulse racing your hand can't displace the pointer |

**Path movement**

| Setting | Default | Notes |
|---|---|---|
| Enabled | off | |
| Pattern | Circle | Circle, square, figure eight, random walk, or custom points |
| Centred on | the cursor | Or the middle of the virtual desktop. Ignored for custom points |
| Size / radius | 120 px | 10–2000; waypoints are clamped to the desktop |
| Speed | 600 px/s | 40–8000; frames are emitted at ~66 fps |
| Minimum / maximum interval | 60 s / 180 s | Independent of the pulse interval |
| Return the cursor | on | Puts the pointer back when the run finishes |
| Points | — | Absolute screen coordinates as `x,y` pairs, e.g. `200,300  900,450  600,800`. Separate them with spaces, newlines, semicolons or `->`; malformed pairs are skipped. Max 256, and a run is capped at 60 s whatever the geometry says. Fewer than two usable points switches the action off at save time and tells you so |

**Auto-click**

| Setting | Default | Notes |
|---|---|---|
| Enabled | off | Only ever switched on by you |
| Button | Left | Left, right, or middle |
| Interval | 1000 ms | 10 ms–10 min, on its own timer |
| Click points | — | Absolute `x,y` pairs, same free-form format as the path. Empty means "click in place". A list that parses to nothing is dropped at save time and the window says so |
| Travel speed | 900 px/s | 40–8000. Only applies when click points are set; one hop is capped at 30 s |
| Double click | off | |

Verified against a live window rather than assumed: single, right and double clicks
all deliver the correct event stream 8/8. Note that a WinForms `Button` never raises
`DoubleClick` from *any* input source — it reports two `Click`s — so a double click
looking like two singles is the control's behaviour, not the injector's.

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

101 tests over the pure core. The load-bearing ones assert the invariant the whole
app rests on: `FakeDesktop` resets its input timestamp on synthetic input exactly
as `GetLastInputInfo` does, then the suite proves ten simulated minutes of pulsing
leaks zero genuine events and never stalls the genuine-idle clock.

`SimultaneousActionTests` covers the parts that involve real threads: that a pulse
and a path run can start on the same tick, that each action keeps its own cadence,
that a path aborts and the clicker disarms when the pointer moves under them, and
that the adaptive ladder adds path movement — but never clicking — when input stops
landing. It also proves the click points are visited in order and cycled, that a
lower travel speed really does produce more frames rather than the same jump, and
that an empty point list never moves the pointer at all. It waits on conditions
rather than assuming an ordering.

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
