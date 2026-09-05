local M = {}

local function fail(message)
	print("PHYS2D_PHASE3_FAIL " .. message)
	os.exit(1, true)
end

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.onFrame()
	local world = phys2d_world("phase3", {
		gravity = { x = 0, y = 0 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})
	world:begin()

	local ground = world:body("ground", {
		type = STATIC,
		initial = { x = 0, y = -0.2 },
	})
	ground:box("solid", {
		hx = 4,
		hy = 0.2,
		filter = { category = 0, mask = "all" },
	})

	local ball = world:body("ball", {
		type = DYNAMIC,
		initial = { x = 1, y = 0.6 },
	})
	ball:circle("solid", {
		r = 0.2,
		density = 1,
		filter = { category = 1, mask = "all" },
	})

	local cast = world:cast_mover({
		ax = 0,
		ay = 1.2,
		bx = 0,
		by = 2.0,
		r = 0.2,
		dx = 0,
		dy = -2.0,
		filter = { mask = { 0 } },
	})
	if type(cast.fraction) ~= "number" or cast.fraction <= 0 or cast.fraction >= 1 then
		fail("cast_mover fraction out of range: " .. tostring(cast.fraction))
	end
	if cast.dy >= 0 then
		fail("cast_mover did not return downward safe delta")
	end

	local visited_plane = false
	local planes = world:collide_mover({
		ax = 0,
		ay = 0.1,
		bx = 0,
		by = 0.9,
		r = 0.2,
		filter = { mask = { 0 } },
	}, function(plane)
		visited_plane = visited_plane or plane.body == "ground"
		return true
	end)
	if not visited_plane or #planes < 1 then
		fail("collide_mover missed ground plane")
	end
	if planes[1].body ~= "ground" or type(planes[1].nx) ~= "number" then
		fail("collide_mover plane snapshot missing shape view")
	end
	local bad_planes, bad_planes_err = world:collide_mover({
		ax = 0,
		ay = 0.1,
		bx = 0,
		by = 0.9,
		r = 0.2,
		filter = { mask = { 0 } },
	}, function()
		error("collide_mover visitor boom")
	end)
	if bad_planes ~= nil or type(bad_planes_err) ~= "string" or not bad_planes_err:find("phys2d_collide_mover") then
		fail("collide_mover visitor error did not return nil,error")
	end
	local mutating_planes, mutating_planes_err = world:collide_mover({
		ax = 0,
		ay = 0.1,
		bx = 0,
		by = 0.9,
		r = 0.2,
		filter = { mask = { 0 } },
	}, function()
		ball:set_velocity({ x = 0, y = 0 })
		return true
	end)
	if
		mutating_planes ~= nil
		or type(mutating_planes_err) ~= "string"
		or not mutating_planes_err:find("physics mutation")
	then
		fail("collide_mover visitor mutation was not rejected")
	end

	world:explode({
		x = 0,
		y = 0.6,
		radius = 2,
		falloff = 0,
		impulse_per_length = 20,
		filter = { mask = { 1 } },
	})
	world:step(1 / 60)

	local velocity = ball:velocity()
	if velocity.x <= 0.1 then
		fail("explode did not push ball outward: vx=" .. tostring(velocity.x))
	end

	begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
	end_pass()

	print(
		"PHYS2D_PHASE3_OK fraction="
			.. string.format("%.4f", cast.fraction)
			.. " planes="
			.. #planes
			.. " vx="
			.. string.format("%.4f", velocity.x)
	)
	quit()
end

return M
