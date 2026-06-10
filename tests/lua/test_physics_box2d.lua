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

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sokol", width = 320, height = 180 })
end

function M.onFrame()
	frame = frame + 1
	trace("frame " .. frame .. " world")

	local world = phys2d_world("smoke", {
		gravity = { x = 0, y = -10 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})
	trace("begin")
	phys2d_begin(world)

	trace("ground body")
	local ground = phys2d_body(world, "ground", {
		type = STATIC,
		initial = { x = 0, y = -0.25 },
	})
	trace("ground shape")
	phys2d_box(ground, "solid", {
		hx = 4,
		hy = 0.25,
		density = 0,
		friction = 0.8,
		contact = true,
	})

	trace("ball body")
	local ball = phys2d_body(world, "ball", {
		type = DYNAMIC,
		initial = { x = 0, y = 2.0 },
	})
	trace("ball shape")
	phys2d_circle(ball, "solid", {
		r = 0.25,
		density = 1,
		friction = 0.4,
		contact = true,
	})

	trace("step")
	phys2d_step(world, 1 / 60)

	trace("pose")
	local pose = phys2d_pose(ball)
	if not pose then
		fail("pose missing")
	end

	trace("contacts begin")
	local contacts = phys2d_contacts(world, "begin")
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

	begin_pass({ target = main_tex, clear_color = { 0.02, 0.03, 0.04, 1.0 } })
	end_pass()

	if frame == 30 and not (pose.y < 1.0) then
		fail("ball did not fall by frame 30: y=" .. tostring(pose.y))
	end

	if saw_contact then
		print("PHYS2D_SMOKE_OK frame=" .. frame .. " y=" .. string.format("%.4f", pose.y))
		quit()
		return
	end

	if frame > 180 then
		fail("contact begin not observed")
	end
end

return M
