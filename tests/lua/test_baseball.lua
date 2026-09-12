-- Run after: bash scripts/run-cs-sample.sh 24_baseball --build
local entry = arg[1] or "samples/24_baseball/.lub/Baseball24.lua"
lub = {
	gfx = {
		begin_pass = function() end,
		end_pass = function() end,
	},
}

local function fresh()
	local g = dofile(entry)
	g.rng = Rand.new(0x0B5EBA11)
	g.reset_actors()
	g.cam_eye = Vec3.new(6, 5.2, -9)
	g.cam_target = Vec3.new(0, 1, 9)
	return g
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
local start_x, start_z = g.bx, g.bz
for _ = 1, 6 do
	g.simulate_tick()
end
assert(math.abs(g.bx - start_x) < 0.01 and math.abs(g.bz - start_z) < 0.01, "throw must wait for the fielder's release")
advance(g, function()
	return g.play_phase == 3
end, "first-base decision")
assert(g.event_text == "OUT!" or g.event_text == "SAFE!", "ground ball must resolve at first base")
local cover = g.fielders[g.first_base_cover + 1]
assert(g.ball_held_by == g.first_base_cover, "the covering fielder must receive the throw")
assert((cover.x - 19.4) ^ 2 + (cover.z - 19.4) ^ 2 < 0.36, "force out requires a fielder on the base")
assert(g.ball_visible, "first baseman must visibly hold the ball at the decision")
assert(actor_count(g) == 10, "runner must remain visible at the first-base decision")
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
advance(g, function()
	assert(g.cam_eye.x == eye.x and g.cam_eye.y == eye.y and g.cam_eye.z == eye.z, "pitch camera must stay fixed")
	return g.state == 5
end, "catcher receives pitch")
assert(g.bz <= -2 and g.ball_visible, "called pitch must reach the catcher and remain visible")

g = fresh()
for _ = 1, 18000 do
	g.simulate_tick()
	for _, f in ipairs(g.fielders) do
		assert(f.x * f.x + f.z * f.z < 76 * 76, "fielder must stay inside the fence")
	end
	assert(g.cam_eye.y > 3 and g.cam_eye.z < 0, "camera must stay above the backstop, behind home")
end
print("baseball: home-run lifecycle, actor continuity, pitch, throw, decision hold, fence and camera PASS")
