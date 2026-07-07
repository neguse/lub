local M = {}

local function fail(message)
	print("PHYS2D_JOINTS_FAIL " .. message)
	os.exit(1, true)
end

local function has_joint(items, key, kind)
	for _, item in ipairs(items) do
		if item.joint == key and item.type == kind and item.valid then
			return true
		end
	end
	return false
end

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.onFrame()
	local world = phys2d_world("joints", {
		gravity = { x = 0, y = 0 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})
	world:begin()

	local ground = world:body("ground", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	ground:box("solid", { hx = 0.2, hy = 0.2 })

	local function dynamic_body(key, x, y)
		local b = world:body(key, {
			type = DYNAMIC,
			initial = { x = x, y = y },
		})
		b:circle("solid", { r = 0.12, density = 1 })
		return b
	end

	local bob = dynamic_body("bob", 0, 1)
	local rotor = dynamic_body("rotor", 1, 0)
	local slider = dynamic_body("slider", -1, 0)
	local wheel = dynamic_body("wheel", -1, 1)
	local panel = dynamic_body("panel", 1, 1)
	local motor_body = dynamic_body("motor_body", -2, 1)
	local mouse_body = dynamic_body("mouse_body", 2, 1)
	local filter_body = dynamic_body("filter_body", 2, 0)

	local distance = world:joint("distance", {
		type = "distance",
		a = ground,
		b = bob,
		length = 1.0,
		spring = { enabled = true, hertz = 2.0, damping_ratio = 0.7 },
		motor = { enabled = true, speed = 0.0, max_force = 5.0 },
	})
	local hinge = world:joint("hinge", {
		type = "revolute",
		a = ground,
		b = rotor,
		motor = { enabled = true, speed = 1.0, max_torque = 3.0 },
		limit = { enabled = true, lower = -0.5, upper = 0.5 },
	})
	local prismatic = world:joint("slider_joint", {
		type = "prismatic",
		a = ground,
		b = slider,
		axis = { x = 1, y = 0 },
		limit = { enabled = true, lower = -0.5, upper = 0.5 },
		motor = { enabled = true, speed = 0.1, max_force = 4.0 },
	})
	world:joint("wheel_joint", {
		type = "wheel",
		a = ground,
		b = wheel,
		axis = { x = 0, y = 1 },
		spring = { enabled = true, hertz = 2.0, damping_ratio = 0.7 },
	})
	world:joint("weld_joint", {
		type = "weld",
		a = ground,
		b = panel,
		linear_hertz = 2.0,
		angular_hertz = 2.0,
	})
	world:joint("motor_joint", {
		type = "motor",
		a = ground,
		b = motor_body,
		linear_offset = { x = 0.1, y = 0.1 },
		max_force = 5.0,
		max_torque = 5.0,
	})
	world:joint("mouse_joint", {
		type = "mouse",
		a = ground,
		b = mouse_body,
		target = { x = 2.1, y = 1.0 },
		hertz = 2.0,
		max_force = 5.0,
	})
	world:joint("filter_joint", {
		type = "filter",
		a = ground,
		b = filter_body,
	})

	hinge:set_motor({ enabled = true, speed = 0.5, max_torque = 4.0 })
	hinge:set_limit({ enabled = true, lower = -0.6, upper = 0.6 })
	distance:set_spring({ enabled = true, hertz = 3.0, damping_ratio = 0.8 })
	prismatic:set_target({ translation = 0.1 })

	local info = hinge:info()
	if info.type ~= "revolute" or info.a ~= "ground" or info.b ~= "rotor" then
		fail("joint info missing hinge endpoint data")
	end
	if type(hinge:angle()) ~= "number" then
		fail("revolute angle readback missing")
	end
	if type(hinge:motor_torque()) ~= "number" then
		fail("revolute motor torque readback missing")
	end
	if type(distance:length()) ~= "number" then
		fail("distance length readback missing")
	end
	if type(prismatic:translation()) ~= "number" or type(prismatic:speed()) ~= "number" then
		fail("prismatic readback missing")
	end
	if not has_joint(ground:joints(), "hinge", "revolute") then
		fail("body joint enumeration missing hinge")
	end

	world:step(1 / 60)
	local counters = world:counters()
	if counters.joint_count < 8 then
		fail("expected 8 joints, got " .. tostring(counters.joint_count))
	end

	begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
	end_pass()

	print("PHYS2D_JOINTS_OK joints=" .. counters.joint_count .. " angle=" .. string.format("%.4f", hinge:angle()))
	quit()
end

return M
