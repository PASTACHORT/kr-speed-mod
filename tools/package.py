"""Build one Nexus Mods zip per game into dist/.  Usage: python tools/package.py"""
import os, shutil, zipfile

VERSION = "1.0.0"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DIST = os.path.join(ROOT, "dist")

LOVE_GAMES = [
    # (zip name, game title, save folder in %APPDATA%, slider colours)
    ("KingdomRush", "Kingdom Rush", "kingdom_rush", "gold and brown"),
    ("KingdomRushFrontiers", "Kingdom Rush Frontiers", "kingdom_rush_frontiers", "desert sand and turquoise"),
    ("KingdomRushOrigins", "Kingdom Rush Origins", "kingdom_rush_origins", "elven green and silver"),
    ("KingdomRushAlliance", "Kingdom Rush 5: Alliance TD", "kingdom_rush_alliance", "royal blue and gold"),
    ("KingdomRushGenesis", "Kingdom Rush 6: Genesis TD", "kingdom_rush_genesis", "icy blue and silver"),
]

CREDITS = """Credits
-------
Made by lvivskapasta. Built with Claude Code (Anthropic's AI coding assistant): the reverse engineering,
code and installers were written with AI assistance and tested in game.
Kingdom Rush is (c) Ironhide Game Studio. This is an unofficial fan mod and contains no game files.
License: MIT (see LICENSE.txt)."""

LICENSE = """MIT License

Copyright (c) 2026 lvivskapasta

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE
USE OR OTHER DEALINGS IN THE SOFTWARE.
"""


def crlf(s):
    return s.replace("\r\n", "\n").replace("\n", "\r\n")


def love_install_bat(title, folder):
    return f"""@echo off
setlocal
title {title} - Speed Slider installer
set "DEST=%APPDATA%\\{folder}"
if not exist "%DEST%" (
    echo Could not find the save folder of {title}:
    echo   %DEST%
    echo Start the game once, close it, and run this installer again.
    echo.
    pause
    exit /b 1
)
copy /Y "%~dp0director.lua" "%DEST%\\director.lua" >nul
if errorlevel 1 (
    echo Install failed: could not copy director.lua to %DEST%
    pause
    exit /b 1
)
echo Speed Slider v{VERSION} installed for {title}.
echo   %DEST%\\director.lua
echo.
echo Start a level: the slider appears at the top of the battle screen.
pause
"""


def love_uninstall_bat(title, folder):
    return f"""@echo off
setlocal
title {title} - Speed Slider uninstaller
set "DEST=%APPDATA%\\{folder}"
for %%F in (director.lua speedmod_settings.txt speedmod_log.txt speedmod_path_report.txt) do (
    if exist "%DEST%\\%%F" del /F /Q "%DEST%\\%%F"
)
echo Speed Slider removed from {title}. Your saves were not touched.
pause
"""


def love_readme(title, folder, colours):
    return f"""{title} - Speed Slider x1-x8   (v{VERSION})
{'=' * (len(title) + 33)}

Adds a game speed slider to the battle screen, from x1 up to x8.
Slider colours in this game: {colours}.

Features
--------
- Drag or click the slider to pick x1 to x8. Only the battle speeds up; menus and the camera stay normal.
- Click the xN badge to switch between x1 and your last fast speed.
- Drag the badge to move the slider anywhere. The position is remembered.
- Every level and every restart starts at x1.
- When a cutscene takes control away, the speed drops to x1 and the slider hides until it ends.
- When a path opens, closes or extends during a battle, the speed drops to x1 so you can react.

Install
-------
1. Run the game once (so its save folder exists), then close it.
2. Double-click Install.bat.
   It copies director.lua to %APPDATA%\\{folder}\\
Manual install: copy director.lua into that folder yourself.

Uninstall
---------
Double-click Uninstall.bat, or delete director.lua from %APPDATA%\\{folder}\\
Your saves are never changed.

How it works
------------
The mod is one Lua file placed in the game's save folder. The game loads it at startup alongside its own
code; no game files are modified, so Steam updates and file verification leave it alone.
It drives the game's built-in (unused) developer time multiplier, so the speed-up is exact.

Notes
-----
- Single-player only. Achievements and saves work as normal.
- The save folder is synced by Steam Cloud, so the mod may follow you to other PCs.
- Troubleshooting: the mod writes %APPDATA%\\{folder}\\speedmod_log.txt. If the slider does not appear,
  include that file in your bug report.

{CREDITS}
"""


VENG_INSTALL = """@echo off
setlocal
title Kingdom Rush Vengeance - Speed Slider installer
set "DEST=%LOCALAPPDATA%\\KRVSpeed"
if not exist "%DEST%" mkdir "%DEST%"
taskkill /IM KRVSpeed.exe /F >nul 2>&1
copy /Y "%~dp0KRVSpeed.exe" "%DEST%\\KRVSpeed.exe" >nul
if errorlevel 1 (
    echo Install failed: could not copy KRVSpeed.exe to %DEST%
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s=New-Object -ComObject WScript.Shell;" ^
  "foreach($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))){" ^
  " $l=$s.CreateShortcut((Join-Path $d 'KR Vengeance Speed Slider.lnk'));" ^
  " $l.TargetPath='%DEST%\\KRVSpeed.exe'; $l.WorkingDirectory='%DEST%'; $l.Save() }"
echo.
choice /C YN /M "Start the slider automatically when Windows starts"
if errorlevel 2 goto nostartup
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$s=New-Object -ComObject WScript.Shell;" ^
  "$l=$s.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Startup')) 'KR Vengeance Speed Slider.lnk'));" ^
  "$l.TargetPath='%DEST%\\KRVSpeed.exe'; $l.WorkingDirectory='%DEST%'; $l.Save()"
:nostartup
start "" "%DEST%\\KRVSpeed.exe"
echo.
echo Speed Slider v@VERSION@ installed to %DEST%
echo It is running now. Start Kingdom Rush Vengeance and begin a level.
echo Next time, start it from the "KR Vengeance Speed Slider" shortcut before or after the game.
pause
""".replace("@VERSION@", VERSION)

VENG_UNINSTALL = """@echo off
setlocal
title Kingdom Rush Vengeance - Speed Slider uninstaller
taskkill /IM KRVSpeed.exe /F >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "foreach($d in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('Startup'))){" ^
  " $p=Join-Path $d 'KR Vengeance Speed Slider.lnk'; if(Test-Path $p){ Remove-Item $p -Force } }"
if exist "%LOCALAPPDATA%\\KRVSpeed" rmdir /S /Q "%LOCALAPPDATA%\\KRVSpeed"
for %%F in (speedmod_settings.txt speedmod_log.txt) do (
    if exist "%APPDATA%\\Kingdom Rush Vengeance\\%%F" del /F /Q "%APPDATA%\\Kingdom Rush Vengeance\\%%F"
)
echo Speed Slider removed. The game and your saves were not touched.
pause
"""

VENG_README = f"""Kingdom Rush Vengeance - Speed Slider x1-x8   (v{VERSION})
==========================================================

Adds a game speed slider (x1 up to x8) on top of the battle screen, in Vengeance colours
(dark purple and toxic green).

Features
--------
- Drag or click the slider to pick x1 to x8.
- Click the xN badge to switch between x1 and your last fast speed.
- Drag the badge to move the slider. The position is remembered.
- Every level starts at x1. Outside battles the game always runs at normal speed.
- The slider only shows during battles, while the game window is active, and never takes focus
  from the game.

Install
-------
Double-click Install.bat. It copies KRVSpeed.exe to %LOCALAPPDATA%\\KRVSpeed, adds desktop and
Start-menu shortcuts, can optionally start with Windows, and starts the slider.
Or just run KRVSpeed.exe from anywhere; it waits for the game in the background.

Uninstall
---------
Double-click Uninstall.bat (removes the program, shortcuts and its settings).

How it works
------------
Kingdom Rush Vengeance runs on its own C++ engine (cocos2d-x), so this is a small companion app instead
of a script mod. It finds the game's clock in memory and sets its speed multiplier, and draws the slider
in its own small window on top of the game. Nothing is injected and no game files are changed.
When the app closes, the game goes back to normal speed.

Notes
-----
- Single-player game; works offline.
- Requires Windows 10/11 64-bit. No .NET install needed (it is bundled).
- The whole game clock is scaled, so battle HUD animations speed up too.
- Use windowed or borderless mode. The slider cannot draw over exclusive fullscreen.
- Some antivirus tools flag apps that change another program's memory (trainers). This app only
  writes the game's speed value.
- Troubleshooting: %APPDATA%\\Kingdom Rush Vengeance\\speedmod_log.txt shows what the app sees.
- Tested with game version 1.16.4.0. A game update can move things in memory; if the slider
  stops working after an update, report it with the log file.

{CREDITS}
"""


def write_zip(name, files):
    os.makedirs(DIST, exist_ok=True)
    path = os.path.join(DIST, f"{name}-SpeedSlider-v{VERSION}.zip")
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        for arc, data in files.items():
            if isinstance(data, str):
                z.writestr(arc, data.encode("utf-8"))
            else:
                z.write(data[0], arc)
    print("built", path, os.path.getsize(path) // 1024, "KB")


def main():
    if os.path.isdir(DIST):
        shutil.rmtree(DIST)
    lua = os.path.join(ROOT, "src", "director.lua")
    for name, title, folder, colours in LOVE_GAMES:
        write_zip(name, {
            "director.lua": (lua,),
            "Install.bat": crlf(love_install_bat(title, folder)),
            "Uninstall.bat": crlf(love_uninstall_bat(title, folder)),
            "README.txt": crlf(love_readme(title, folder, colours)),
            "LICENSE.txt": crlf(LICENSE),
        })
    write_zip("KingdomRushVengeance", {
        "KRVSpeed.exe": (os.path.join(ROOT, "vengeance", "publish", "KRVSpeed.exe"),),
        "Install.bat": crlf(VENG_INSTALL),
        "Uninstall.bat": crlf(VENG_UNINSTALL),
        "README.txt": crlf(VENG_README),
        "LICENSE.txt": crlf(LICENSE),
    })


if __name__ == "__main__":
    main()
