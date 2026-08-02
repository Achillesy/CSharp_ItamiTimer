# ItamiTimer

A desktop focus timer with **enforced constraints**, for Windows and macOS.

Classic pomodoro apps trust you to stay on task; ItamiTimer doesn't. It reads your actual
window activity from [ActivityWatch](https://activitywatch.net/) and only counts the minutes
you *provably* spent inside the goal you committed to — with you actually at the keyboard.
Wander off to a browser, a chat app, or away from the desk, and those minutes silently don't
count: the grey deadline arc on the clock face slides further away, and you watch it go.

The name (window title 「一袋米要扛几楼」) is a meme about pain. **All the pain comes from the
dial** — no popups, no sounds on violation, no report card, no numbers. You look, you guess.

## The pomodoro technique, and how this differs

The classic technique (Cirillo, 1980s): pick a task, set a 25-minute timer, work until it
rings, take a 5-minute break; every four rounds take a longer break. Its two known weak
points are that the timer measures *elapsed* time, not *focused* time, and that compliance
is entirely on the honor system.

ItamiTimer keeps the frame — one commitment, one focus block, one earned break — and
replaces the honor system with an audit:

| | Classic pomodoro | ItamiTimer |
|---|---|---|
| What counts | Wall-clock time | Only seconds matching your chosen goal, while present |
| Timer state | A countdown in memory | **No countdown exists.** The dial is a projection of ActivityWatch history |
| Slacking | Timer keeps running | Seconds don't count; the deadline arc slides forward — the task takes longer |
| Being away (AFK) | Timer keeps running | Doesn't count, and isn't blamed — drawn as a hollow dashed box, not red |
| Punishment | None | Time itself. Nothing is voided, nothing beeps; you just finish later |
| Break | Fixed 5 min | ⌈focus ÷ 5⌉ min, starting the moment completion is detected |
| Long break every 4 rounds | Yes | No — one task = one focus + one rest, then the program **stops and waits** |
| Auto-start next round | Common | **Never.** Starting a task is always your act |
| Report | Varies | None on screen, ever. The dial's coloured cells are the whole story |

There is **no degraded mode**. If ActivityWatch can't be reached the program keeps running in
constrained mode and the judgment model absorbs it: seconds with no ActivityWatch record at
all are counted as focus, on the grounds that ActivityWatch's own outages shouldn't be
charged to you. The cost of that choice is documented in `DESIGN.md` §3.1.

Because the dial is derived — not accumulated — polling frequency never affects accuracy, and
the whole accounting engine is a pure function you can unit-test with synthetic events.

## Requirements

- **[ActivityWatch](https://activitywatch.net/) running locally**, with **both** watchers:
  `aw-watcher-window` (what's focused) and `aw-watcher-afk` (are you there). The afk watcher
  is not optional — window events keep growing via heartbeats even when nobody is at the desk,
  so without afk data "walk away with the right window focused" would be invisible free time.
- .NET 10 runtime (SDK to build).

## Build & run

```bash
dotnet build ItamiTimer.slnx
dotnet test ItamiTimer.slnx
```

Publish for Windows (RID required — without it the output balloons to ~560 MB of
every platform's native Skia/HarfBuzz binaries):

```bash
dotnet publish src/ItamiTimer.App -c Release -r win-x64 --self-contained false -o "$LOCALAPPDATA/Programs/ItamiTimer"
```

Result: ~27 MB / 35 files. Publishing strips the two giant native `.pdb` files
automatically (an MSBuild target in the csproj — `dotnet publish` alone does not).

macOS — must be packed as a `.app` bundle (icon, Dock, and the `DOTNET_ROOT`
environment for Finder-launched apps):

```bash
./pack-macos.sh
```

The focus-length slider is **10–50 minutes in Release** and **3–10 minutes in Debug**, so a
development build doesn't make you sit through 25 real minutes to see the completion moment.

## Defining your goals: `rules.json`

A goal is a small, tight whitelist — a few apps or a title pattern, not a broad category.
Goals appear as radio buttons; **pick exactly one** before you start, and it's locked for the
round.

```json
{
  "groups": {
    "Economics": {
      "rules": [
        { "title": "(?i)econom|mankiw" },
        { "app": "EconReader\\.exe" }
      ]
    },
    "Coding": {
      "rules": [
        { "app": "devenv\\.exe|Code\\.exe|^Code$" }
      ]
    },
    "Last quarter": { "disabled": true, "rules": [ { "title": "Blender" } ] }
  },

  "executeCommand": {
    "windows": [ "explorer", "rundll32.exe user32.dll,LockWorkStation", "shutdown /s /t 0" ],
    "macos":   [ "open -a TextEdit", "pmset displaysleepnow", "osascript -e 'tell application \"System Events\" to shut down'" ]
  }
}
```

- Rules within a goal are **OR**; `app` and `title` inside one rule are **AND**.
  A title-only rule means "doing this thing counts, whatever the tool".
- Anything that isn't a match is **off-task** (fail-closed). There is no neutral list —
  add a rule rather than loosening the model.
- `"disabled": true` hides a stale goal without deleting it.
- Windows and macOS app names live in the same file; a pattern that can't match on the
  other platform is harmless.
- `executeCommand` is optional — it's what the alarm's **Execute** switch runs when the
  yellow hand comes due (see below). **Only the first entry ever runs**: keep your
  collection there and move one to the top to use it. Without it, nothing runs.
- Comments and trailing commas are allowed (JSONC). **The program never writes this file**,
  so your comments survive.
- Lookup order: `%LOCALAPPDATA%\ItamiTimer\rules.json` (yours, survives republish) →
  next to the exe → working directory.

## The alarm

Scroll the mouse wheel **on the clock face** — there is no button. A yellow hand appears and
the ring time is fixed the moment you stop turning. Slow scrolling moves one minute per
notch; keep scrolling and it accelerates up to 30 minutes per notch, so a full 12-hour sweep
takes about twenty flicks. It's one-shot: once it rings, it's gone.

In Settings you can switch the alarm from **a sound** to **Execute**, which runs
`executeCommand` for the current OS. The two are mutually exclusive, and Execute is
force-reset to off at every launch — a shutdown command must never survive a restart.

## Project layout

```
src/ItamiTimer.Core/    net10.0  the accounting engine: rules, judgment buffer, projections.
                                 No UI, no platform calls — enforced by the csproj.
src/ItamiTimer.Cli/     net10.0  `itami` — dry-run the engine against real ActivityWatch data.
                                 The only place that ever prints a report.
src/ItamiTimer.App/     net10.0  Avalonia UI: dial, dominoes, sounds, alarm, settings.
tests/                  xUnit    pure-function tests: synthetic events, no waiting.
```

Everything the program draws — dial, dominoes, window-chrome icons, the exe icon itself — is
computed vector geometry. The repository contains no bitmap or audio assets; even the
tick-tock is synthesized at runtime (white noise burst + damped sine), and notification
sounds come from whatever the OS already ships.

Runtime data:

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\ItamiTimer\` |
| macOS | `~/Library/Application Support/ItamiTimer/` |

| File | Written by | Contents |
|---|---|---|
| `rules.json` | **you**, by hand | your goals, and optionally `executeCommand` |
| `settings.json` | the program | sound choices, switches, the alarm time |
| `during.json` | the program | accumulated focus seconds per goal |
| `itami.log` | the program | 1 MB rolling; the UI is silent, so this is the only place to find out what happened |

Task state is **never** written to disk. Closing the program abandons the current round —
though the time you did earn is still added to `during.json`.

## Dry-running the engine

```bash
itami replay --since "2026-07-27 14:00" --until "2026-07-27 15:30" --minutes 25 --group Economics
```

This replays real ActivityWatch history through **the same engine and the same one-minute
tick** the app uses, and prints the cells plus a report. It's the fastest way to check
whether your rules actually match what you do.

`itami start` runs a live round in the terminal; `itami bench` exercises the engine on
synthetic events with no ActivityWatch at all.

## Debug exits

Three CLI switches that render off-screen and exit (no window, safe for CI):

```bash
ItamiTimer --dial-specimens <dir>    # render the dial in key states as PNGs
ItamiTimer --export-icon <path.ico>  # export the vector icon as the exe icon
ItamiTimer --export-iconset <dir>    # same, as .iconset for macOS iconutil
```

## Design documentation

- [`DESIGN.md`](./DESIGN.md) — the full system design: judgment model, covering algorithm,
  archiving, dial rendering spec, alarm model, cross-platform notes, known bugs, backlog.
- [`DECISIONS.md`](./DECISIONS.md) — the guardrail list: decisions that were made
  deliberately, with their known costs, and must not be casually reversed.

Both are written in Chinese; the code comments are too.

## License

Apache 2.0 — see [`LICENSE`](./LICENSE).
