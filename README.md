# ItamiTimer

A desktop focus timer with **enforced constraints**, for Windows and macOS.

Classic pomodoro apps trust you to stay on task; ItamiTimer doesn't. It reads your actual
window activity from [ActivityWatch](https://activitywatch.net/) and only counts the minutes
you *provably* spent inside the apps you committed to — with you actually at the keyboard.
Wander off to a browser, a chat app, or away from the desk, and those minutes silently don't
count: the deadline arc on the clock face slides further away, and you watch it go.

The name (窗口标题「一袋米要扛几楼」) is a meme about pain. **All the pain comes from the
dial** — no popups, no sounds on violation, no report card, no numbers. You look, you guess.

## The pomodoro technique, and how this differs

The classic technique (Cirillo, 1980s): pick a task, set a 25-minute timer, work until it
rings, take a 5-minute break; every four rounds take a longer break. Its two known weak
points are that the timer measures *elapsed* time, not *focused* time, and that compliance
is entirely on the honor system.

ItamiTimer keeps the frame — one commitment, one focus block, one earned break — and
replaces the honor system with an audit:

| | Classic pomodoro | ItamiTimer (constrained mode) |
|---|---|---|
| What counts | Wall-clock time | Only intervals matching your checked goals, while present |
| Timer state | A countdown in memory | **No countdown exists.** State = pure function(task record, ActivityWatch history, now) |
| Slacking | Timer keeps running | Minutes don't count; the deadline arc slides forward — the task takes longer |
| Being away (AFK) | Timer keeps running | Doesn't count (and isn't blamed — it's drawn as blank, not red) |
| Punishment | None | Time itself. Nothing is voided, nothing beeps; you just finish later |
| Break | Fixed 5 min | ⌊focus ÷ 5⌋ + 1 min, earned at the moment focus completes |
| Long break every 4 rounds | Yes | No — one task = one focus + one rest, then the program **stops and waits** |
| Auto-start next round | Common | **Never.** Starting a task is always your act |
| Report | Varies | None on screen, ever. The dial's colored cells are the whole story |

Because state is derived — not accumulated — polling frequency never affects accuracy,
a temporarily unreachable ActivityWatch loses nothing, and the whole accounting engine is
a pure function you can unit-test with synthetic events.

### Degraded mode: plain pomodoro

Without ActivityWatch installed (or if `rules.json` can't be read), ItamiTimer degrades at
startup into a plain pomodoro clock: every minute counts, the dial stays green, the goal
list disappears. The same replay engine runs on two synthetic events, so Core is unchanged.

The mode is **locked at launch** and never switches mid-session — otherwise killing
aw-server would be a free way to farm focus minutes. If ActivityWatch comes up later,
restart the program.

## Requirements

- **[ActivityWatch](https://activitywatch.net/) running locally** for constrained mode,
  with **both** watchers: `aw-watcher-window` (what's focused) and `aw-watcher-afk`
  (are you there). The afk watcher is not optional — window events keep growing via
  heartbeats even when nobody is at the desk, so without afk data "walk away with the
  right window focused" would be invisible free time.
- .NET 10 runtime (SDK to build).

Without ActivityWatch it still runs as the plain pomodoro clock described above.

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

## Defining your goals: `rules.json`

A goal group is a small, tight whitelist — a few apps or a title pattern, not a broad
category. Groups appear as checkboxes; check the ones allowed for this round.

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
    }
  },
  "ignore": [
    "LockApp\\.exe|^loginwindow$",
    "explorer\\.exe|^Finder$"
  ]
}
```

- Rules within a group are **OR**; `app` and `title` inside one rule are **AND**.
  A title-only rule means "doing this thing counts, whatever the tool".
- `ignore` entries are neutral: they count (file dialogs, the desktop) but are never goals.
- Unknown apps are **off-task** (fail-closed). Add a rule rather than loosening the model.
- Windows and macOS app names live in the same file; a pattern that can't match on the
  other platform is harmless.
- Lookup order: `%LOCALAPPDATA%\ItamiTimer\rules.json` (yours, survives republish) →
  next to the exe → working directory. Comments and trailing commas are allowed.
- Checking another goal mid-task re-labels the **whole** history with the union —
  retroactively. "Got bored of economics, switched to Blender practice" is allowed
  by design; the constraint is *staying inside your declared range*, not monotony.

## Project layout

```
src/ItamiTimer.Core/    net10.0  the accounting engine: rules, replay, projections.
                                 No UI, no platform calls — enforced by the csproj.
src/ItamiTimer.Cli/     net10.0  `itami` — dry-run the engine against real AW data.
                                 The only place that ever prints a report.
src/ItamiTimer.App/     net10.0  Avalonia UI: dial, dominoes, sounds, alarm, settings.
tests/                  xUnit    pure-function tests: synthetic events, no waiting.
```

Everything the program draws — dial, dominoes, tomato icon, chrome icons, the exe icon
itself — is computed vector geometry. The repository contains no bitmap or audio assets;
even the tick-tock is synthesized at runtime (white noise burst + damped sine).

Runtime data (`settings.json`, your `rules.json`, `itami.log`):

| | |
|---|---|
| Windows | `%LOCALAPPDATA%\ItamiTimer\` |
| macOS | `~/Library/Application Support/ItamiTimer/` |

Task state is **never** written to disk. Closing the program abandons the task — the
longest task is 50 minutes; a crash loses one round at most. History bookkeeping belongs
to ActivityWatch itself.

## Debug exits

Three CLI switches that render off-screen and exit (no window, safe for CI):

```bash
ItamiTimer --dial-specimens <dir>    # render the dial in key states as PNGs
ItamiTimer --export-icon <path.ico>  # export the vector tomato as the exe icon
ItamiTimer --export-iconset <dir>    # same, as .iconset for macOS iconutil
```

## Design documentation

- [`DESIGN.md`](./DESIGN.md) — the full system design: judgment model, replay algorithm,
  time model, dial rendering spec, alarm model, cross-platform notes.
- [`DECISIONS.md`](./DECISIONS.md) — the guardrail list: decisions that were made
  deliberately, with their known costs, and must not be casually reversed.

## License

Apache 2.0 — see [`LICENSE`](./LICENSE).
