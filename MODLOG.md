# MODLOG - Kingdom Rush speed slider (x1-x8)

Goal: a game-speed slider up to x8 in every Kingdom Rush game on this PC.

## Games (Steam, E:\SteamLibrary\steamapps\common)
| Game | AppID | Engine | Lua archive | Save dir (%APPDATA%) | LuaJIT |
|---|---|---|---|---|---|
| Kingdom Rush | 246420 | LÖVE | zip appended to `Kingdom Rush.exe` | kingdom_rush | 2.0 (bc v1) |
| Frontiers | 458710 | LÖVE | exe | kingdom_rush_frontiers | 2.0 |
| Origins | 816340 | LÖVE | exe | kingdom_rush_origins | 2.0 |
| Alliance (KR5) | 2849080 | LÖVE | exe | kingdom_rush_alliance | 2.0 |
| Genesis (KR6) | 4259190 | LÖVE | `main.kra` (zip) | kingdom_rush_genesis | 2.1 GC64 (bc v2, flag 0x08) |
| Vengeance | 1367550 | own native C++ engine (`KR4_PC`, `res`) | - | %LOCALAPPDATA%\Kingdom Rush Vengeance | - |

No anti-cheat in any of them. All single-player.

## Route: save-folder module override (no game files touched)
- `main.lua` init calls `love.filesystem.setIdentity(version.identity)` first (save dir is mounted ahead of the archive),
  sets require path `?.lua;?/init.lua;<game paths>`, and later `load_director` does `require "director"`.
- So `%APPDATA%\<id>\director.lua` is found before `all/director.lua`. Our shim `fs.load`s the real
  `all/director.lua` (skipping save-dir matches) and returns it with hooks. **Confirmed in KR1** (error traceback showed
  `require` -> our file).
- `args.lua` route is dead: only read when `DEBUG` is true.

## Speed hook
`game:update(dt)` (all/game.lua, same in all 5):
KR1-3: `if self.DBG_TIME_MULT then for i=1,self.DBG_TIME_MULT do self.simulation:update(dt) end else ... end; self.game_gui:update(dt)`
KR5/6: same, plus `if mult < 1 then simulation:update(dt*mult)`.
`game` and `director` are globals. Mod sets `game.DBG_TIME_MULT = speed` each frame -> exact xN battle, UI normal speed.

## Tools
- `~/kr-decomp/` extracted bytecode (outside repo, never ship). `~/kr-decomp/ljdis.py` own LuaJIT 2.0/2.1 disassembler.
- `tools/luacheck.py <game dir> file.lua` compile-checks with the game's own lua51.dll.
- Downloading luajit-decompiler-v2.exe was blocked by permissions; not needed.

## Backups (restore with `um backup restore`)
`~/.universal-modder/backups/<id>-saves/20261030-0916xx.zip` for all five save folders.

## Notes
- Save folders have `steam_autocloud.vdf`: director.lua / speedmod_settings.txt may sync through Steam Cloud.
- KR1 shows a launcher (settings screen) before the director loads; mod log appears after Play.

## Status
- [x] KR1 loads the shim
- [x] KR1 battle: slider visible and speed works (confirmed by user)
- [x] Installed into KR2, KR3, KR5, KR6 with per-game colour themes (KR_GAME)
- [x] KR6 Genesis loads the shim (log)
- [ ] User check of the slider in KR2/KR3/KR5/KR6 battles

## Lessons
- KR1 (and KR6) love.dll is LÖVE 0.x: setColor takes 0-255. Panel was drawn but invisible at 0-1 values.
  The mod detects love.getVersion() major < 11 and scales colours.
- The panel's click area worked while invisible (speed got set to x7 by accident). Keep it in mind when debugging.

## x1 on level start and after cutscenes
- New level = director.active_item.store is a new table, or store.tick_ts jumps backwards (restart).
- Cutscenes via hump.signal (global registry, `require "hump.signal"`): KR1-3 game_gui registers "hide-gui"/"show-gui";
  KR5/6 also "start-cinematic"/"end-cinematic" (from level scripts). Start -> speed x1, slider hidden, DBG_TIME_MULT locked 1.
- Handlers re-registered at each new level (register is keyed by function, idempotent).
- [ ] Verify in game: log lines "speed reset to x1 (new level)" / "(cutscene)".

## Release v1.0.0 (Nexus packages)
- `python tools/package.py` -> dist/<Game>-SpeedSlider-v1.0.0.zip (KR1, Frontiers, Origins, Alliance, Genesis, Vengeance).
- LÖVE zips: director.lua + Install.bat / Uninstall.bat (copy to / clean %APPDATA%\<id>) + README.txt + LICENSE.txt (MIT).
- Vengeance zip: self-contained single-file KRVSpeed.exe (dotnet publish win-x64) + Install.bat (to %LOCALAPPDATA%\KRVSpeed,
  desktop/Start-menu shortcuts, optional startup) + Uninstall.bat.
- `um publish check` PASS on all six. KR1 Install.bat verified. Vengeance Install.bat not run (creates shortcuts).
- Author name in package.py CREDITS/LICENSE is "lvivskapasta" - change if publishing under another name.

## Vengeance (KRVSpeed)
- cocos2d-x statically linked. Vtables via RTTI (`.?AVScheduler@cocos2d@@` etc.), live objects by vtable scan.
- Scheduler::_timeScale at +0x28; Director::_runningScene at +0x150; battle = scene vtable KRGameplayScene. Game v1.16.4.0.
- Game window is WS_EX_TOPMOST: overlay re-raises itself every tick (SetWindowPos HWND_TOPMOST, NOACTIVATE).
- Not done: cutscene / path-change resets (candidates: KRSkill*Cutscene* classes, KRPathSegmentController).
