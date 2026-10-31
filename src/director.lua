-- Kingdom Rush Speed Slider (x1 - x8) by lvivskapasta
-- Works in Kingdom Rush, Frontiers, Origins, Alliance and Genesis (the LÖVE-based games).
--
-- How it loads: LÖVE searches the game's save folder before the game archive, and the game
-- resolves `require "director"` through the pattern "?.lua" first. This file therefore stands
-- in for the director module: it runs the game's real `all/director.lua`, then hooks the battle.
--
-- How it speeds up: every game's `game:update(dt)` runs `self.simulation:update(dt)` once per
-- unit of `game.DBG_TIME_MULT` (a leftover developer time-warp). Setting it to N runs the
-- battle N times per frame, so towers, enemies, cooldowns and waves all go N times faster
-- while menus and the camera stay normal.
--
-- Remove this file from the save folder to uninstall.

local MOD_NAME = "speedmod"
local MAX_SPEED = 8
local SETTINGS_FILE = "speedmod_settings.txt"

---------------------------------------------------------------------------------------------
-- 1. Load the real director
---------------------------------------------------------------------------------------------

local fs = love.filesystem
local save_dir = fs.getSaveDirectory()

local LOG_FILE = "speedmod_log.txt"
pcall(fs.write, LOG_FILE, "")
local function log(msg)
    msg = "[" .. MOD_NAME .. "] " .. tostring(msg)
    print(msg)
    pcall(fs.append, LOG_FILE, os.date("%H:%M:%S ") .. msg .. "\n")
end

local function find_original()
    -- Walk the game's require path, skipping any match that lives in the save folder (us).
    for pattern in fs.getRequirePath():gmatch("[^;]+") do
        local path = pattern:gsub("%?", "director")
        local is_file
        if fs.getInfo then is_file = fs.getInfo(path, "file") ~= nil else is_file = fs.isFile(path) end
        if is_file and fs.getRealDirectory(path) ~= save_dir then
            return path
        end
    end
    return "all/director.lua"
end

local original_path = find_original()
local chunk = assert(fs.load(original_path))
local director = chunk("director")
director = director or rawget(_G, "director")

---------------------------------------------------------------------------------------------
-- 2. State and settings
---------------------------------------------------------------------------------------------

local state = {
    speed = 1,        -- current multiplier (integer 1..MAX_SPEED)
    last_fast = 2,    -- what the badge toggles back to
    px = nil, py = nil, -- panel centre as a fraction of the screen (nil = default spot)
    dragging = nil,   -- "knob" or "panel"
    drag_dx = 0, drag_dy = 0,
    last_draw = nil,  -- time the battle last drew (panel only exists in battle)
    frame = 0,
    fonts = {},
    rect = nil,
    cutscene = false, -- true while the game has taken control away (GUI hidden / cinematic)
    level_store = nil, -- the battle's store table; a new one means a new level
}

local function clamp(v, lo, hi) if v < lo then return lo elseif v > hi then return hi end return v end

local function load_settings()
    local ok, data = pcall(fs.read, SETTINGS_FILE)
    if not ok or type(data) ~= "string" then return end
    local s, lf, px, py = data:match("speed=(%d+)%s+last=(%d+)%s+px=([%d%.%-]+)%s+py=([%d%.%-]+)")
    if s then
        state.speed = clamp(tonumber(s) or 1, 1, MAX_SPEED)
        state.last_fast = clamp(tonumber(lf) or 2, 2, MAX_SPEED)
        local x, y = tonumber(px), tonumber(py)
        if x and y and x >= 0 and y >= 0 then state.px, state.py = x, y end
    end
end

local function save_settings()
    pcall(fs.write, SETTINGS_FILE, string.format("speed=%d last=%d px=%.4f py=%.4f\n",
        state.speed, state.last_fast, state.px or -1, state.py or -1))
end

load_settings()

local function set_speed(n)
    n = clamp(math.floor(n + 0.5), 1, MAX_SPEED)
    if n ~= state.speed then
        state.speed = n
        if n > 1 then state.last_fast = n end
        save_settings()
    end
end

-- back to x1 (the badge still remembers the last fast speed for a one-click return)
local function reset_speed(why)
    state.dragging = nil
    if state.speed ~= 1 then
        state.speed = 1
        save_settings()
    end
    log("speed reset to x1 (" .. why .. ")")
end

---------------------------------------------------------------------------------------------
-- Cutscenes: the games announce them through hump.signal. KR1-3 hide the HUD with
-- "hide-gui"/"show-gui"; Alliance and Genesis also send "start-cinematic"/"end-cinematic".
---------------------------------------------------------------------------------------------

local function on_cutscene_start()
    if not state.cutscene then
        state.cutscene = true
        reset_speed("cutscene")
    end
end

local function on_cutscene_end()
    state.cutscene = false
end

local function register_signals()
    local ok, signal = pcall(require, "hump.signal")
    if not ok or type(signal) ~= "table" or type(signal.register) ~= "function" then
        log("hump.signal not available, cutscene reset disabled")
        return
    end
    -- hump.signal keys handlers by function, so registering again is harmless
    for _, name in ipairs({ "hide-gui", "start-cinematic" }) do signal.register(name, on_cutscene_start) end
    for _, name in ipairs({ "show-gui", "end-cinematic" }) do signal.register(name, on_cutscene_end) end
end

---------------------------------------------------------------------------------------------
-- Path changes: every game keeps its paths in the shared `path_db` module. A path opening,
-- closing, or getting a new start/end point mid-battle drops the speed to x1.
---------------------------------------------------------------------------------------------

local PATH_FIELDS = { "active_paths", "path_start_node", "path_end_node",
                      "visible_path_start_node", "visible_path_end_node" }
local PATH_SETUP_TIME = 2     -- battle seconds treated as map setup (changes ignored)
local PATH_EVENT_GAP = 3      -- changes closer than this (real seconds) are one event

local path_shadow = {}
local path_last_change = nil

-- Frequency report: path events per level, measured in battle time (independent of speed).
-- Levels with more than 2 events inside any 60 s window are written to speedmod_path_report.txt.
local REPORT_FILE = "speedmod_path_report.txt"
local path_report = { level = nil, times = {}, flagged = false, max_in_window = 0 }

local function report_level_start(level)
    path_report.level = level
    path_report.times = {}
    path_report.flagged = false
    path_report.max_in_window = 0
end

local function report_event(battle_time)
    local t = path_report.times
    t[#t + 1] = battle_time
    local n = 0
    for i = #t, 1, -1 do
        if battle_time - t[i] <= 60 then n = n + 1 else break end
    end
    if n > path_report.max_in_window then path_report.max_in_window = n end
    log(string.format("path change #%d in level %s at %.0f s (%d in the last minute)",
        #t, tostring(path_report.level), battle_time, n))
    if n > 2 and not path_report.flagged then
        path_report.flagged = true
        pcall(fs.append, REPORT_FILE, string.format("%s %s level %s: %d path changes within one minute (battle time %.0f s)\n",
            os.date("%Y-%m-%d %H:%M"), tostring(rawget(_G, "KR_GAME")), tostring(path_report.level), n, battle_time))
    end
end

-- copies path_db's state into path_shadow; returns true when anything differs
local function path_sync(P)
    local changed = false
    for _, f in ipairs(PATH_FIELDS) do
        local live = rawget(P, f)
        if type(live) == "table" then
            local shadow = path_shadow[f]
            if not shadow then shadow = {}; path_shadow[f] = shadow; changed = true end
            for k, v in pairs(live) do
                if shadow[k] ~= v then shadow[k] = v; changed = true end
            end
            for k in pairs(shadow) do
                if live[k] == nil then shadow[k] = nil; changed = true end
            end
        end
    end
    return changed
end

local function check_paths(battle_time, baseline)
    local P = package.loaded["path_db"]
    if type(P) ~= "table" then return end
    local changed = path_sync(P)
    if baseline or not changed or (battle_time or 0) < PATH_SETUP_TIME then return end
    local now = love.timer.getTime()
    local new_event = path_last_change == nil or now - path_last_change > PATH_EVENT_GAP
    path_last_change = now
    if new_event then
        report_event(battle_time or 0)
        reset_speed("path changed")
    end
end

---------------------------------------------------------------------------------------------
-- 3. The slider panel
---------------------------------------------------------------------------------------------

local function font(size)
    size = math.max(8, math.floor(size))
    if not state.fonts[size] then state.fonts[size] = love.graphics.newFont(size) end
    return state.fonts[size]
end

local function layout()
    local sw, sh = love.graphics.getDimensions()
    local s = sh / 1080
    local w, h = math.floor(330 * s), math.floor(46 * s)
    local cx = (state.px or 0.5) * sw
    local cy = (state.py or (60 / 1080)) * sh
    local x = clamp(math.floor(cx - w / 2), 0, sw - w)
    local y = clamp(math.floor(cy - h / 2), 0, sh - h)
    local badge_w = math.floor(62 * s)
    local pad = math.floor(14 * s)
    local r = {
        s = s, x = x, y = y, w = w, h = h,
        badge = { x = x + math.floor(6 * s), y = y + math.floor(6 * s), w = badge_w, h = h - math.floor(12 * s) },
        track_x0 = x + badge_w + pad + math.floor(8 * s),
        track_x1 = x + w - pad,
        track_y = y + math.floor(h * 0.56),
    }
    state.rect = r
    return r
end

local function knob_x(r, speed)
    return r.track_x0 + (r.track_x1 - r.track_x0) * (speed - 1) / (MAX_SPEED - 1)
end

local function speed_at(r, mx)
    local t = (mx - r.track_x0) / (r.track_x1 - r.track_x0)
    return 1 + clamp(t, 0, 1) * (MAX_SPEED - 1)
end

-- LÖVE 0.10 (KR1-3, KR5) takes colours as 0-255, LÖVE 11+ as 0-1
local COLOR_SCALE = 1
do
    local major = love.getVersion and love.getVersion() or 0
    if major < 11 then COLOR_SCALE = 255 end
end
local function color(r, g, b, a)
    local k = COLOR_SCALE
    love.graphics.setColor(r * k, g * k, b * k, (a or 1) * k)
end

-- Per-game colour themes (KR_GAME is set by each game's main_globals.lua)
local THEMES = {
    -- Kingdom Rush: warm gold and brown
    kr1 = { bg = {0.10, 0.07, 0.04, 0.78}, border = {0.86, 0.70, 0.36, 0.95}, badge_on = {0.80, 0.33, 0.10},
            badge_off = {0.28, 0.22, 0.14}, text = {1, 0.95, 0.82}, track = {0.35, 0.28, 0.18},
            fill = {0.95, 0.62, 0.20}, notch_on = {1, 0.85, 0.5}, notch_off = {0.65, 0.56, 0.42},
            knob_rim = {0.15, 0.10, 0.05}, knob = {1, 0.80, 0.35} },
    -- Frontiers: desert sand and turquoise
    kr2 = { bg = {0.05, 0.09, 0.10, 0.80}, border = {0.90, 0.78, 0.52, 0.95}, badge_on = {0.05, 0.60, 0.62},
            badge_off = {0.20, 0.26, 0.26}, text = {1, 0.97, 0.88}, track = {0.30, 0.27, 0.20},
            fill = {0.20, 0.82, 0.80}, notch_on = {0.70, 1, 0.97}, notch_off = {0.70, 0.62, 0.45},
            knob_rim = {0.04, 0.12, 0.13}, knob = {0.95, 0.85, 0.55} },
    -- Origins: elven forest green and silver
    kr3 = { bg = {0.04, 0.09, 0.06, 0.80}, border = {0.78, 0.86, 0.80, 0.95}, badge_on = {0.22, 0.62, 0.30},
            badge_off = {0.16, 0.24, 0.18}, text = {0.94, 1, 0.94}, track = {0.18, 0.28, 0.22},
            fill = {0.52, 0.88, 0.45}, notch_on = {0.86, 1, 0.82}, notch_off = {0.50, 0.62, 0.54},
            knob_rim = {0.05, 0.12, 0.07}, knob = {0.88, 0.94, 0.90} },
    -- Alliance: royal blue and gold
    kr5 = { bg = {0.04, 0.06, 0.14, 0.82}, border = {0.95, 0.80, 0.35, 0.95}, badge_on = {0.20, 0.38, 0.85},
            badge_off = {0.16, 0.20, 0.32}, text = {1, 0.96, 0.85}, track = {0.18, 0.22, 0.38},
            fill = {0.40, 0.62, 1}, notch_on = {0.80, 0.88, 1}, notch_off = {0.48, 0.52, 0.66},
            knob_rim = {0.05, 0.06, 0.15}, knob = {1, 0.82, 0.32} },
    -- Genesis: icy blue and silver
    kr6 = { bg = {0.03, 0.07, 0.12, 0.82}, border = {0.70, 0.88, 1, 0.95}, badge_on = {0.10, 0.52, 0.85},
            badge_off = {0.14, 0.22, 0.32}, text = {0.92, 0.97, 1}, track = {0.14, 0.24, 0.36},
            fill = {0.35, 0.78, 1}, notch_on = {0.80, 0.95, 1}, notch_off = {0.46, 0.58, 0.70},
            knob_rim = {0.03, 0.08, 0.14}, knob = {0.85, 0.95, 1} },
}
local theme = THEMES[rawget(_G, "KR_GAME")] or THEMES.kr1
local function tcolor(c) color(c[1], c[2], c[3], c[4] or 1) end

local function inside(px, py, x, y, w, h) return px >= x and px <= x + w and py >= y and py <= y + h end

local function draw_panel()
    local g = love.graphics
    local r = layout()
    local s = r.s
    g.push("all")
    g.origin()
    g.setScissor()
    g.setLineWidth(math.max(1, 2 * s))

    -- panel
    tcolor(theme.bg)
    g.rectangle("fill", r.x, r.y, r.w, r.h, 10 * s, 10 * s)
    tcolor(theme.border)
    g.rectangle("line", r.x, r.y, r.w, r.h, 10 * s, 10 * s)

    -- badge (click: toggle x1 <-> last speed, drag: move panel)
    local b = r.badge
    local fast = state.speed > 1
    if fast then tcolor(theme.badge_on) else tcolor(theme.badge_off) end
    g.rectangle("fill", b.x, b.y, b.w, b.h, 7 * s, 7 * s)
    tcolor(theme.text)
    local f = font(24 * s)
    g.setFont(f)
    local label = "x" .. state.speed
    g.print(label, math.floor(b.x + (b.w - f:getWidth(label)) / 2), math.floor(b.y + (b.h - f:getHeight()) / 2))

    -- track
    local ty = r.track_y
    tcolor(theme.track)
    g.setLineWidth(math.max(2, 6 * s))
    g.line(r.track_x0, ty, r.track_x1, ty)
    tcolor(theme.fill)
    g.line(r.track_x0, ty, knob_x(r, state.speed), ty)

    -- notches + numbers
    local nf = font(12 * s)
    g.setFont(nf)
    for i = 1, MAX_SPEED do
        local nx = knob_x(r, i)
        if i <= state.speed then tcolor(theme.notch_on) else tcolor(theme.notch_off) end
        g.circle("fill", nx, ty, math.max(2, 3 * s))
        local t = tostring(i)
        g.print(t, math.floor(nx - nf:getWidth(t) / 2), math.floor(r.y + 3 * s))
    end

    -- knob
    local kx = knob_x(r, state.speed)
    tcolor(theme.knob_rim)
    g.circle("fill", kx, ty, 11 * s)
    tcolor(theme.knob)
    g.circle("fill", kx, ty, 8.5 * s)
    g.pop()
end

local function panel_visible()
    if state.cutscene then return false end
    return state.last_draw ~= nil and love.timer.getTime() - state.last_draw < 0.25
end

local traced = {}
local function trace_once(what)
    if not traced[what] then traced[what] = true; log("first call: " .. what) end
end

-- returns true when the click was ours
local function on_press(x, y, button)
    if button ~= 1 or not panel_visible() or not state.rect then return false end
    local r = state.rect
    if not inside(x, y, r.x, r.y, r.w, r.h) then return false end
    local b = r.badge
    if inside(x, y, b.x, b.y, b.w, b.h) then
        state.dragging = "panel"
        state.press_x, state.press_y, state.moved = x, y, false
        state.drag_dx = x - (r.x + r.w / 2)
        state.drag_dy = y - (r.y + r.h / 2)
    else
        state.dragging = "knob"
        set_speed(speed_at(r, x))
    end
    return true
end

local function on_release(x, y, button)
    if not state.dragging then return false end
    if state.dragging == "panel" then
        if state.moved then
            save_settings()
        else
            -- a plain click on the badge toggles normal speed / last fast speed
            if state.speed > 1 then state.last_fast = state.speed; state.speed = 1 else state.speed = state.last_fast end
            save_settings()
        end
    end
    state.dragging = nil
    return true
end

local function on_drag()
    if not state.dragging or not state.rect then return end
    if not love.mouse.isDown(1) then
        on_release(0, 0, 1)
        return
    end
    local mx, my = love.mouse.getPosition()
    if state.dragging == "knob" then
        set_speed(speed_at(state.rect, mx))
    else
        if math.abs(mx - state.press_x) + math.abs(my - state.press_y) > 6 then state.moved = true end
        if state.moved then
            local sw, sh = love.graphics.getDimensions()
            state.px = clamp((mx - state.drag_dx) / sw, 0, 1)
            state.py = clamp((my - state.drag_dy) / sh, 0, 1)
        end
    end
end

---------------------------------------------------------------------------------------------
-- 4. Hook the battle (`game` is created lazily by the game, so patch it when it appears)
---------------------------------------------------------------------------------------------

local function patch_game(game)
    if type(game) ~= "table" or rawget(game, "__speedmod") then return end
    rawset(game, "__speedmod", true)

    local orig_update = game.update
    game.update = function(self, dt, ...)
        trace_once("game.update")
        on_drag()
        self.DBG_TIME_MULT = state.cutscene and 1 or state.speed
        return orig_update(self, dt, ...)
    end

    local orig_draw = game.draw
    game.draw = function(self, ...)
        trace_once("game.draw")
        local a, b, c = orig_draw(self, ...)
        state.last_draw = love.timer.getTime()
        if state.cutscene then return a, b, c end
        local ok, err = pcall(draw_panel)
        if not ok then log("draw error: " .. tostring(err)) else trace_once("draw_panel") end
        return a, b, c
    end

    local orig_press = game.mousepressed
    if orig_press then
        game.mousepressed = function(self, x, y, button, ...)
            if on_press(x, y, button) then return true end
            return orig_press(self, x, y, button, ...)
        end
    end

    local orig_release = game.mousereleased
    if orig_release then
        game.mousereleased = function(self, x, y, button, ...)
            if on_release(x, y, button) then return true end
            return orig_release(self, x, y, button, ...)
        end
    end

    log("battle hooked (" .. (game == rawget(_G, "game") and "global game" or "game instance") .. "), speed x" .. state.speed)
end

if type(director) == "table" and type(director.update) == "function" then
    local orig_director_update = director.update
    director.update = function(self, ...)
        state.frame = state.frame + 1
        -- the battle screen is whatever item the director is showing that owns a simulation
        local item = rawget(self, "active_item")
        if type(item) == "table" and item.simulation ~= nil and item.game_gui ~= nil then
            patch_game(item)
            local store = item.store
            local ts = type(store) == "table" and tonumber(rawget(store, "tick_ts")) or nil
            local restarted = ts ~= nil and state.level_ts ~= nil and ts + 0.5 < state.level_ts
            if ts then state.level_ts = ts end
            if store ~= nil and (store ~= state.level_store or restarted) then
                state.level_store = store
                state.level_ts = ts
                state.cutscene = false
                register_signals()
                reset_speed("new level")
                path_last_change = nil
                report_level_start(type(store) == "table" and rawget(store, "level_idx") or "?")
                check_paths(ts, true)
            else
                check_paths(ts, false)
            end
        end
        return orig_director_update(self, ...)
    end
    log(tostring(rawget(_G, "KR_GAME")) .. " theme, LOVE " .. table.concat({love.getVersion and love.getVersion() or "?"}, ".") .. " colour scale " .. COLOR_SCALE .. "; loaded from " .. save_dir .. " (original director: " .. original_path .. ")")
else
    log("director not found, mod inactive")
end

return director
