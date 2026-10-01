-- lubx.FixedStep の key / mouse の edge が tick を消費したあとに消えることを見る。
-- lub.input の key_pressed / mouse_pressed を差し替え、1 frame だけ押されたことに
-- して 3 tick 回し、edge が最初の tick にしか見えないことを確かめる。
package.path = "samples/?.lua;" .. package.path
local lubx = require("lubx")

local M = {}

local function fail(message)
	print("FIXEDSTEP_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local pressed_now = false
local step = lubx.FixedStep.new(60, 1)
local frame = 0
local ticks = 0

function M.on_init()
	lub.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 320,
		height = 180,
	})
	-- runtime の frame latch の代わり: pressed_now の frame だけ "r" と左ボタンが押された
	lub.input.key_pressed = function(key)
		return pressed_now and key == "r"
	end
	lub.input.key_released = function()
		return false
	end
	lub.input.mouse_pressed = function(button)
		return pressed_now and button == 1
	end
	lub.input.mouse_released = function()
		return false
	end
end

local function tick()
	ticks = ticks + 1
	local want = ticks == 1
	expect(
		(step:key_pressed("r") and true or false) == want,
		"tick " .. ticks .. ": key_pressed(r) must be " .. tostring(want)
	)
	expect(not step:key_pressed("space"), "tick " .. ticks .. ": key_pressed(space) must stay false")
	expect(
		(step:mouse_pressed(1) and true or false) == want,
		"tick " .. ticks .. ": mouse_pressed(1) must be " .. tostring(want)
	)
	expect(not step:key_released("r"), "tick " .. ticks .. ": key_released(r) must stay false")
end

function M.on_frame()
	frame = frame + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	pressed_now = frame == 1
	-- dt = tick_dt なので 1 frame に 1 tick 走る
	step:frame(step.tick_dt, tick)
	expect(ticks == frame, "frame " .. frame .. " must run exactly one tick (ran " .. ticks .. ")")
	if frame == 3 then
		print("FIXEDSTEP_OK")
		lub.quit()
	end
end

return M
