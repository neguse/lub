-- Run after: bash scripts/run-cs-sample.sh 24_baseball --build
local entry = arg[1] or "samples/24_baseball/.lub/Baseball24.lua"
lub = {
	KEY_DOWN = "key_down",
	KEY_UP = "key_up",
	input = {
		key_released = function()
			return false
		end,
		mouse_pressed = function()
			return false
		end,
		mouse_released = function()
			return false
		end,
		key_pressed = function()
			return false
		end,
	},
	gfx = {
		begin_pass = function() end,
		end_pass = function() end,
	},
}

local function fresh()
	local g = dofile(entry)
	g.rng = Rand.new(0x0B5EBA11)
	g.reset_actors()
	g.cam_eye = Vec3.new(2.8, 2.6, -5.2)
	g.cam_target = Vec3.new(-0.1, 1.1, 5)
	return g
end

local function project(g, x, y, z)
	local vp = Camera3d.vp({ eye = g.cam_eye, target = g.cam_target, fov = g.cam_fov, aspect = 16 / 9 })
	local clip = vp * Vec4.new(x, y, z, 1)
	assert(clip.w > 0, "shot subject must be in front of the camera")
	return clip.x / clip.w, clip.y / clip.w
end

local function in_frame(g, x, y, z)
	local sx, sy = project(g, x, y, z)
	assert(math.abs(sx) < 0.95 and math.abs(sy) < 0.95, "shot subject must remain inside the frame")
end

local function advance(g, predicate, label)
	for _ = 1, 6000 do
		g.simulate_tick()
		if predicate() then
			return
		end
	end
	error("timed out: " .. label)
end

local function actor_count(g)
	local count = 0
	g.reloaded = false
	g.ren = Renderer3d.new("test")
	g.ren.begin = function() end
	g.ren.draw = function() end
	g.ren.end_ = function() end
	g.step = { frame = function() end }
	g.draw_hud = function() end
	g.draw_char = function()
		count = count + 1
	end
	g.on_frame(0)
	return count
end

local g = fresh()
g.state = 1
g.update_camera(0)
local _, feet = project(g, -0.85, 0, 0)
local _, head_top = project(g, -0.85, 1.9, 0)
assert((head_top - feet) * 270 > 280, "pre-pitch shot must show the batter close up")
in_frame(g, -0.85, 0, 0)
in_frame(g, -0.85, 1.9, 0)
g.char_mesh = { { data = { bones = { { name = "torso" }, { name = "head" } } } } }
local model, bones
g.ren = {
	draw = function(_, _, m, opts)
		model, bones = m, opts.bones
	end,
}
g.draw_char(-0.85, 0, math.pi / 2, 0, g.pose_swing(0.30))
local torso, head = Mat4.new(), Mat4.new()
for i = 1, 16 do
	torso.m[i], head.m[i] = bones[i], bones[i + 16]
end
local chest = (model * torso):mul_dir(Vec3.new(0, 0, 1))
local gaze = (model * head):mul_dir(Vec3.new(0, 0, 1))
assert(chest.x > 0.9, "batter must face across home plate, not out of the batter's box")
assert(gaze.z > 0.95 and math.abs(gaze.x) < 0.10, "batter must look toward the pitcher")
local bat = g.bat_matrix(0.52)
local grip = bat:mul_point(Vec3.new(0, 0, 0))
local axis = bat:mul_dir(Vec3.new(0, 0, 1))
local contact = Vec3.new(-grip.x, 1 - grip.y, 0.35 - grip.z)
local along = contact.x * axis.x + contact.y * axis.y + contact.z * axis.z
local miss = (contact.x - along * axis.x) ^ 2 + (contact.y - along * axis.y) ^ 2 + (contact.z - along * axis.z) ^ 2
assert(along > 0.45 and along < 0.94 and miss < 0.0004, "bat barrel must cross the ball at contact")
g.draw_char(0, 0, math.pi / 4, 0, g.pose_run(0))
local forward = model:mul_dir(Vec3.new(0, 0, 1))
assert(forward.x > 0 and forward.z > 0, "runner must face the direction of travel")

g = fresh()
advance(g, function()
	return g.is_home_run and g.state == 4
end, "home run")
local score = g.score[1] + g.score[2]
advance(g, function()
	return g.state ~= 4
end, "home run finishes")
assert(g.score[1] + g.score[2] > score, "home run must reach home before leaving the play")
advance(g, function()
	return g.state == 2
end, "next batter")
assert(actor_count(g) == 10, "next batter must be drawn after a home run")

g = fresh()
g.start_pitch()
g.will_swing = true
g.swing_outcome = 2
g.exit_speed = 28
g.exit_launch = -4
g.exit_spray = 22
advance(g, function()
	return g.state == 4
end, "ground ball")
assert(actor_count(g) == 10, "batter must become one runner, without disappearing or duplicating")
local eye = g.cam_eye
for _ = 1, 6 do
	g.simulate_tick()
end
assert(g.cam_eye.x == eye.x and g.cam_eye.z == eye.z, "contact must remain in the batting shot")
advance(g, function()
	return g.play_phase == 2
end, "fielder gathers the ball")
assert(g.ball_visible, "ball must remain visible during the transfer")
assert(g.first_base_view, "cut to first base before the throwing release")
local throw_eye = g.cam_eye
local start_x, start_z = g.bx, g.bz
for _ = 1, 6 do
	g.simulate_tick()
end
assert(math.abs(g.bx - start_x) < 0.01 and math.abs(g.bz - start_z) < 0.01, "throw must wait for the fielder's release")
advance(g, function()
	assert(g.cam_eye == throw_eye, "hold the first-base shot through the throw and decision")
	in_frame(g, g.bx, g.by, g.bz)
	in_frame(g, 19.4, 0, 19.4)
	local r = g.batter_runner or g.retired_runner
	in_frame(g, r.x, 1, r.z)
	return g.play_phase == 3
end, "first-base decision")
assert(g.event_text == "OUT!" or g.event_text == "SAFE!", "ground ball must resolve at first base")
local cover = g.fielders[g.first_base_cover + 1]
assert(g.ball_held_by == g.first_base_cover, "the covering fielder must receive the throw")
assert((cover.x - 19.4) ^ 2 + (cover.z - 19.4) ^ 2 < 0.36, "force out requires a fielder on the base")
assert(g.ball_visible, "first baseman must visibly hold the ball at the decision")
assert(actor_count(g) == 10, "runner must remain visible at the first-base decision")
_, feet = project(g, cover.x, 0, cover.z)
_, head_top = project(g, cover.x, 1.9, cover.z)
assert((head_top - feet) * 270 > 70, "first-base decision must be larger than the field overview")
for _ = 1, 12 do
	g.simulate_tick()
end
assert(g.state == 4, "hold the deciding play before returning to the batter")

g = fresh()
g.start_pitch()
g.will_swing = false
g.pitch_in_zone = true
advance(g, function()
	return g.state == 3
end, "pitch release")
eye = g.cam_eye
_, feet = project(g, -0.85, 0, 0)
_, head_top = project(g, -0.85, 1.9, 0)
assert((head_top - feet) * 270 > 175, "pitch shot must keep the batter large in the foreground")
in_frame(g, 0, 1, 18.44)
advance(g, function()
	assert(g.cam_eye.x == eye.x and g.cam_eye.y == eye.y and g.cam_eye.z == eye.z, "pitch camera must stay fixed")
	in_frame(g, g.bx, g.by, g.bz)
	return g.state == 5
end, "catcher receives pitch")
assert(g.bz <= -2 and g.ball_visible, "called pitch must reach the catcher and remain visible")

g = fresh()
for _ = 1, 18000 do
	g.simulate_tick()
	for _, f in ipairs(g.fielders) do
		assert(f.x * f.x + f.z * f.z < 76 * 76, "fielder must stay inside the fence")
	end
	local cam = g.cam_eye
	assert(cam.y > 1 and cam.x * cam.x + cam.z * cam.z < 76 * 76, "camera must stay inside the stadium")
	assert(cam.z > -8 or cam.y > 3, "camera behind home must clear the backstop")
	if g.first_base_view and g.state == 4 then
		in_frame(g, g.bx, g.by, g.bz)
		in_frame(g, 19.4, 0, 19.4)
		local r = g.batter_runner or g.retired_runner
		in_frame(g, r.x, 1, r.z)
	end
end
print("baseball: home-run lifecycle, actor continuity, pitch, throw, decision hold, fence and camera PASS")

local pressed
lub.ui = {
	set_next_window = function() end,
	begin_window = function()
		return true
	end,
	end_window = function() end,
	text = function() end,
	separator = function() end,
	same_line = function() end,
	render = function() end,
	button = function(label)
		return label == pressed
	end,
	checkbox = function(_, value)
		return value
	end,
	slider_float = function(_, value)
		return value
	end,
	slider_int = function(_, value)
		return value
	end,
}
lub.gfx.size = function()
	return 960, 540
end
g = fresh()
g.reloaded = false
g.ren = Renderer3d.new("debug-test")
g.ren.begin = function() end
g.ren.draw = function() end
g.ren.end_ = function() end
g.debug_box = {}
g.debug_props = false
g.debug_guides = false
g.draw_hud = function() end
local shown_pose
g.draw_char = function(_, _, _, _, pose)
	shown_pose = pose
end
g.on_event({ kind = lub.KEY_DOWN, key = 59 })
assert(g.model_debug, "F2 must open the model viewer")
g.on_event({ kind = lub.KEY_DOWN, key = 59 })
assert(g.model_debug, "holding F2 must not repeatedly toggle the viewer")
g.on_event({ kind = lub.KEY_UP, key = 59 })
local match_time = g.t_accum
pressed = "Contact"
g.on_frame(1 / 60)
assert(g.t_accum == match_time, "model viewer must pause the match")
assert(math.abs(g.debug_time - 0.52 * 0.55) < 0.00001, "contact button must seek the batting contact")
local expected_pose = g.pose_swing(0.52)
for i, value in ipairs(expected_pose) do
	assert(math.abs(shown_pose[i] - value) < 0.00001, "viewer must render the match's swing pose")
end
pressed = "+1 frame"
g.on_frame(1 / 60)
assert(math.abs(g.debug_time - 0.52 * 0.55 - 1 / 60) < 0.00001, "step must advance one match tick")
pressed = nil
g.debug_playing = true
g.debug_speed = 0.25
local before = g.debug_time
g.on_frame(1 / 60)
assert(math.abs(g.debug_time - before - 1 / 240) < 0.00001, "slow playback must advance only the preview clock")
assert(g.t_accum == match_time, "preview playback must not advance the match")
pressed = "Bind pose"
g.on_frame(1 / 60)
for _, value in ipairs(shown_pose) do
	assert(value == 0, "bind pose must disable every animation transform")
end
pressed = "Return to match"
g.on_frame(1 / 60)
assert(not g.model_debug, "return button must close the viewer")
pressed = nil
g.on_frame(1 / 60)
assert(g.t_accum > match_time and g.t_accum < match_time + 0.02, "return must resume without catching up paused time")
print("baseball: model preview pause, shared pose, contact seek, frame step, slow playback and resume PASS")
