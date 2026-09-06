local M = {}

local frame = 0
local saw_contact = false
local trace_enabled = os.getenv("LUB_PHYS2D_TRACE") == "1"

local function trace(message)
	if trace_enabled then
		print("PHYS2D_TRACE " .. message)
	end
end

local function fail(message)
	print("PHYS2D_SMOKE_FAIL " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.on_frame()
	frame = frame + 1
	trace("frame " .. frame .. " world")

	local world = lub.phys2d.world("smoke", {
		gravity = { x = 0, y = -10 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})
	trace("begin")
	lub.phys2d.begin(world)

	trace("ground body")
	local ground = lub.phys2d.body(world, "ground", {
		type = lub.phys2d.STATIC,
		initial = { x = 0, y = -0.25 },
	})
	trace("ground shape")
	lub.phys2d.box(ground, "solid", {
		hx = 4,
		hy = 0.25,
		density = 0,
		friction = 0.8,
		contact = true,
	})

	trace("ball body")
	local ball = lub.phys2d.body(world, "ball", {
		type = lub.phys2d.DYNAMIC,
		initial = { x = 0, y = 2.0 },
	})
	trace("ball shape")
	lub.phys2d.circle(ball, "solid", {
		r = 0.25,
		density = 1,
		friction = 0.4,
		contact = true,
	})

	trace("step")
	lub.phys2d.step(world, 1 / 60)

	trace("pose")
	local pose = lub.phys2d.pose(ball)
	if not pose then
		fail("pose missing")
	end

	trace("contacts begin")
	local contacts = lub.phys2d.contacts(world, "begin")
	trace("contacts count " .. tostring(#contacts))
	for _, contact in ipairs(contacts) do
		local a = contact.a.body .. "/" .. contact.a.shape
		local b = contact.b.body .. "/" .. contact.b.shape
		if (a == "ball/solid" and b == "ground/solid") or (a == "ground/solid" and b == "ball/solid") then
			saw_contact = true
		end
	end
	if saw_contact and #ball:contacts() == 0 then
		fail("body contact enumeration missing ball contact")
	end
	trace("contacts done")

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.02, 0.03, 0.04, 1.0 } })
	lub.gfx.end_pass()

	if frame == 30 and not (pose.y < 1.0) then
		fail("ball did not fall by frame 30: y=" .. tostring(pose.y))
	end

	if saw_contact then
		print("PHYS2D_SMOKE_OK frame=" .. frame .. " y=" .. string.format("%.4f", pose.y))
		lub.quit()
		return
	end

	if frame > 180 then
		fail("contact begin not observed")
	end
end

return M
