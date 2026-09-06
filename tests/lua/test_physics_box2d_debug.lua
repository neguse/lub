local M = {}

local frame = 0

local function fail(message)
	print("PHYS2D_DEBUG_FAIL " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.on_frame()
	frame = frame + 1

	local world = lub.phys2d.world("debug", {
		gravity = { x = 0, y = -10 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})
	world:begin()

	local ground = world:body("ground", {
		type = lub.phys2d.STATIC,
		initial = { x = 0, y = -1 },
	})
	ground:box("box", { hx = 2, hy = 0.1, density = 0 })
	ground:segment("rail", { ax = -2, ay = 0.3, bx = 2, by = 0.3 })

	local ball = world:body("ball", {
		type = lub.phys2d.DYNAMIC,
		initial = { x = -0.5, y = 1 },
	})
	ball:circle("circle", { r = 0.2, density = 1 })

	local capsule_body = world:body("capsule", {
		type = lub.phys2d.DYNAMIC,
		initial = { x = 0.6, y = 1.2 },
	})
	capsule_body:capsule("capsule", {
		ax = -0.2,
		ay = 0,
		bx = 0.2,
		by = 0,
		r = 0.1,
		density = 1,
	})

	world:step(1 / 60)

	local dbg = world:debug({ shapes = true, bounds = true, mass = true })
	if type(dbg) ~= "table" then
		fail("debug result missing")
	end
	if #dbg.polygons < 10 then
		fail("expected polygon data, got " .. tostring(#dbg.polygons))
	end
	if #dbg.circles < 7 then
		fail("expected circle data, got " .. tostring(#dbg.circles))
	end
	if #dbg.capsules < 9 then
		fail("expected capsule data, got " .. tostring(#dbg.capsules))
	end
	if #dbg.segments < 8 then
		fail("expected segment data, got " .. tostring(#dbg.segments))
	end
	local profile = world:profile()
	if type(profile.step) ~= "number" or profile.step < 0 then
		fail("profile step missing")
	end
	local counters = world:counters()
	if counters.body_count < 3 then
		fail("expected body counters, got " .. tostring(counters.body_count))
	end
	if counters.shape_count < 4 then
		fail("expected shape counters, got " .. tostring(counters.shape_count))
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.01, 0.015, 0.02, 1.0 } })
	lub.gfx.end_pass()

	print(
		"PHYS2D_DEBUG_OK polygons="
			.. #dbg.polygons
			.. " circles="
			.. #dbg.circles
			.. " capsules="
			.. #dbg.capsules
			.. " segments="
			.. #dbg.segments
			.. " bodies="
			.. counters.body_count
			.. " shapes="
			.. counters.shape_count
	)
	lub.quit()
end

return M
