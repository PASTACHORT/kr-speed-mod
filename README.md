# Kingdom Rush Speed Slider (x1 - x8)

A battle-speed slider, from x1 up to x8, for every Kingdom Rush game on PC (Steam).
Each game gets its own colour theme.

| Game | How it works | Folder |
|---|---|---|
| Kingdom Rush | Lua module in the save folder | [`src/`](src) |
| Kingdom Rush Frontiers | Lua module in the save folder | [`src/`](src) |
| Kingdom Rush Origins | Lua module in the save folder | [`src/`](src) |
| Kingdom Rush 5: Alliance TD | Lua module in the save folder | [`src/`](src) |
| Kingdom Rush 6: Genesis TD | Lua module in the save folder | [`src/`](src) |
| Kingdom Rush Vengeance | Companion overlay app (C#) | [`vengeance/`](vengeance) |

## Features
- Drag or click the track to pick x1 to x8. Only the battle speeds up; menus stay normal.
- Click the xN badge to switch between x1 and your last fast speed.
- Drag the badge to move the panel. Its position is remembered per game.
- Every level (and every restart) starts at x1.
- When a cutscene takes control away, speed drops to x1 and the slider hides until it ends.
- When a path opens, closes or extends mid-battle, speed drops to x1 so you can react (LÖVE games).

## Install (players)
Download the zip for your game from [Releases](../../releases) and run `Install.bat`.
Each zip has a README with manual install and uninstall steps.

### From this repo
LÖVE games (KR1, Frontiers, Origins, Alliance, Genesis). This installs into every game it finds:

    powershell -ExecutionPolicy Bypass -File install.ps1
    powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall

Start each game once first so its save folder (`%APPDATA%\kingdom_rush*`) exists.

Vengeance: build `KRVSpeed.exe` (below) and run it. It waits for the game in the background.

## How it works

### Kingdom Rush 1, Frontiers, Origins, Alliance, Genesis (LÖVE / LuaJIT)
LÖVE searches the game's save folder before the game archive, and the game loads
`require "director"` through `?.lua` first. So [`src/director.lua`](src/director.lua), placed in
`%APPDATA%\<game>\`, stands in for the director module: it runs the game's real
`all/director.lua`, then hooks the battle.

Every game's `game:update(dt)` runs the simulation once per unit of `game.DBG_TIME_MULT`,
a leftover developer time-warp. The mod sets it to the slider value each frame, so towers,
enemies, cooldowns and waves run exactly N times faster while the UI stays normal.
No game files are changed and Steam updates leave the mod alone.

### Kingdom Rush Vengeance (native cocos2d-x)
Vengeance has its own C++ engine, so [`vengeance/KRVSpeed`](vengeance/KRVSpeed) is a small
WinForms overlay. It finds cocos2d's `Scheduler` and `Director` objects through MSVC RTTI
vtables, sets `Scheduler::_timeScale` while a `KRGameplayScene` is running, and draws the
slider in its own topmost window. Nothing is injected. Tested with game version 1.16.4.0;
a game update may move the offsets.

## Build
- **KRVSpeed.exe** (.NET 10 SDK):

      dotnet publish vengeance/KRVSpeed -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o vengeance/publish

- **Release zips:** `python tools/package.py` writes one zip per game to `dist/`
  (build `KRVSpeed.exe` first).
- **Lua syntax check** with a game's own LuaJIT: `python tools/luacheck.py "<game folder>" src/director.lua`

## Repo layout
| Path | What |
|---|---|
| `src/director.lua` | The mod for the five LÖVE games (one file, detects which game it runs in) |
| `install.ps1` | Installs / uninstalls the Lua mod for every game found |
| `vengeance/KRVSpeed/` | Vengeance overlay app (C#, WinForms) |
| `vengeance/rtti.py`, `memscan.py` | Read-only research tools used to find the Vengeance offsets |
| `tools/package.py` | Builds the release zips |
| `tools/luacheck.py` | Compile-checks Lua with the game's own `lua51.dll` |
| `tools/winshot.ps1` | Screenshot of a game window, for testing |
| `MODLOG.md` | Development notes: engines, hooks, offsets, lessons |

## Troubleshooting
The Lua mod writes `%APPDATA%\<game>\speedmod_log.txt`. Vengeance writes
`%APPDATA%\Kingdom Rush Vengeance\speedmod_log.txt`. Attach the log when you open an issue.

## Notes
- All games are single-player with no anti-cheat. Saves and achievements work as normal.
- The LÖVE save folders sync through Steam Cloud, so the mod may follow you to other PCs.
- Some antivirus tools flag apps that write another program's memory (like trainers).
  KRVSpeed only writes the game's speed value, and all of its source is here.

## Credits and license
Made by lvivskapasta. Built with Claude Code (Anthropic's AI coding assistant): the reverse
engineering, code and installers were written with AI help and tested in game.

Kingdom Rush is (c) Ironhide Game Studio. This is an unofficial fan mod; it contains no game
files or game code.

Code is under the [MIT License](LICENSE).
