local M = {}

local frame = 0
local kept_ref = nil
local kept_shape_ref = nil
local stale_chain_ref = nil
local recreate_ref = nil
local stale_joint_ref = nil

local function fail(message)
	print("PHYS2D_LIFETIME_FAIL " .. message)
	os.exit(1, true)
end

local function part_matches(part, body, shape)
	return part and part.body == body and part.shape == shape
end

local function destroyed_pair(event, lhs_body, lhs_shape, rhs_body, rhs_shape)
	local direct = part_matches(event.a, lhs_body, lhs_shape) and part_matches(event.b, rhs_body, rhs_shape)
	local swapped = part_matches(event.b, lhs_body, lhs_shape) and part_matches(event.a, rhs_body, rhs_shape)
	if not direct and not swapped then
		return false
	end
	return event.a.valid == false and event.b.valid == false
end

local function destroyed_sensor(event, sensor_body, sensor_shape, visitor_body, visitor_shape)
	return part_matches(event.sensor, sensor_body, sensor_shape)
		and part_matches(event.visitor, visitor_body, visitor_shape)
		and event.sensor.valid == false
		and event.visitor.valid == false
end

local function expect_not_found(label, fn)
	local value, err = fn()
	if value ~= nil or err ~= "not found" then
		fail(label .. " did not return not found")
	end
end

local function check_missing_world_queries()
	local missing_world = { __lub_kind = "phys2d_world", key = "missing_world" }
	expect_not_found("missing world info", function()
		return phys2d_world_info(missing_world)
	end)
	expect_not_found("missing world pose", function()
		return phys2d_pose(missing_world, "body")
	end)
	expect_not_found("missing world contacts", function()
		return phys2d_contacts(missing_world, "begin")
	end)
	expect_not_found("missing world body events", function()
		return phys2d_body_events(missing_world)
	end)
	expect_not_found("missing world sensors", function()
		return phys2d_sensors(missing_world, "begin")
	end)
	expect_not_found("missing world raycast", function()
		return phys2d_raycast(missing_world, { x = 0, y = 0, dx = 1, dy = 0 })
	end)
	expect_not_found("missing world overlap", function()
		return phys2d_overlap_aabb(missing_world, { min_x = -1, min_y = -1, max_x = 1, max_y = 1 })
	end)
	expect_not_found("missing world shape cast", function()
		return phys2d_shape_cast(missing_world, { type = "circle", x = 0, y = 0, r = 0.1, dx = 1, dy = 0 })
	end)
	expect_not_found("missing world cast mover", function()
		return phys2d_cast_mover(missing_world, { ax = 0, ay = 0, bx = 0, by = 1, r = 0.1, dx = 1, dy = 0 })
	end)
	expect_not_found("missing world collide mover", function()
		return phys2d_collide_mover(missing_world, { ax = 0, ay = 0, bx = 0, by = 1, r = 0.1 })
	end)
	expect_not_found("missing world debug", function()
		return phys2d_debug(missing_world, { shapes = true })
	end)
	expect_not_found("missing world profile", function()
		return phys2d_profile(missing_world)
	end)
	expect_not_found("missing world counters", function()
		return phys2d_counters(missing_world)
	end)
end

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.onFrame()
	frame = frame + 1

	local world = phys2d_world("lifetime", {
		gravity = { x = 0, y = 0 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
	})

	if frame == 1 then
		world:begin({ prune = false })
		local kept = world:body("kept", {
			type = DYNAMIC,
			initial = { x = 1, y = 2 },
		})
		kept_ref = kept
		kept_shape_ref = kept:box("solid", { hx = 0.1, hy = 0.1 })
		local recreate = world:body("recreate", {
			version = 1,
			type = DYNAMIC,
			initial = { x = 3, y = 0 },
		})
		recreate_ref = recreate
		recreate:box("solid", { hx = 0.1, hy = 0.1 })
		local joint_a = world:body("joint_a", {
			type = DYNAMIC,
			initial = { x = 10, y = 0 },
		})
		local joint_b = world:body("joint_b", {
			type = DYNAMIC,
			initial = { x = 12, y = 0 },
		})
		stale_joint_ref = world:joint("stale_joint", {
			type = "distance",
			a = joint_a,
			b = joint_b,
			length = 2,
		})
		local versioned_joint_a = world:body("versioned_joint_a", {
			type = DYNAMIC,
			initial = { x = 14, y = 0 },
		})
		local versioned_joint_b = world:body("versioned_joint_b", {
			type = DYNAMIC,
			initial = { x = 16, y = 0 },
		})
		world:joint("versioned_joint", {
			version = 1,
			type = "distance",
			a = versioned_joint_a,
			b = versioned_joint_b,
			length = 2,
			collide_connected = false,
		})
		local chain_body = world:body("chain_body", {
			type = STATIC,
			initial = { x = 0, y = -2 },
		})
		stale_chain_ref = chain_body:chain("terrain", {
			version = 1,
			points = {
				{ x = -2, y = 0 },
				{ x = -1, y = 0 },
				{ x = 1, y = 0 },
				{ x = 2, y = 0 },
			},
		})
		world:body("hash_body", {
			type = DYNAMIC,
			initial = { x = -1, y = 0 },
		})
		world:body("explicit_body", {
			version = 1,
			type = DYNAMIC,
			initial = { x = -3, y = 0 },
		})
		local versioned_shape_body = world:body("versioned_shape", {
			type = STATIC,
			initial = { x = -5, y = 0 },
		})
		versioned_shape_body:box("solid", {
			version = 1,
			hx = 0.2,
			hy = 0.2,
			contact = false,
			hit = false,
			pre_solve = false,
			sensor_events = false,
		})

		local wall = world:body("wall", {
			type = STATIC,
			initial = { x = 0, y = 0 },
		})
		wall:box("solid", {
			hx = 0.3,
			hy = 0.3,
			contact = true,
			material = "wall",
			user_material_id = 10,
		})
		local crate = world:body("crate", {
			type = DYNAMIC,
			initial = { x = 0, y = 0 },
		})
		crate:box("solid", {
			hx = 0.2,
			hy = 0.2,
			contact = true,
			material = "crate",
			user_material_id = 11,
		})

		local sensor = world:body("sensor", {
			type = STATIC,
			initial = { x = 1, y = 0 },
		})
		sensor:box("zone", {
			hx = 0.3,
			hy = 0.3,
			sensor = true,
			sensor_events = true,
			material = "zone",
			user_material_id = 12,
		})
		local visitor = world:body("visitor", {
			type = DYNAMIC,
			initial = { x = 1, y = 0 },
		})
		visitor:box("solid", {
			hx = 0.2,
			hy = 0.2,
			sensor_events = true,
			material = "visitor",
			user_material_id = 13,
		})

		world:step(1 / 60)
	elseif frame == 2 then
		world:begin({ prune = false })
		recreate_ref:set_velocity({ x = 5, y = 0 })
		local recreated = world:body("recreate", {
			version = 2,
			type = DYNAMIC,
			initial = { x = 3, y = 0 },
		})
		recreated:box("solid", { hx = 0.1, hy = 0.1 })
		world:body("hash_body", {
			type = DYNAMIC,
			initial = { x = -2, y = 0 },
		})
		world:body("explicit_body", {
			version = 1,
			type = DYNAMIC,
			initial = { x = -4, y = 0 },
		})
		local versioned_joint_a = world:body("versioned_joint_a", {
			type = DYNAMIC,
			initial = { x = 14, y = 0 },
		})
		local versioned_joint_b = world:body("versioned_joint_b", {
			type = DYNAMIC,
			initial = { x = 16, y = 0 },
		})
		local versioned_joint = world:joint("versioned_joint", {
			version = 1,
			type = "distance",
			a = versioned_joint_a,
			b = versioned_joint_b,
			length = 2,
			collide_connected = true,
		})
		local versioned_shape_body = world:body("versioned_shape", {
			type = STATIC,
			initial = { x = -5, y = 0 },
		})
		local versioned_shape = versioned_shape_body:box("solid", {
			version = 1,
			hx = 0.5,
			hy = 0.2,
			contact = true,
			hit = true,
			pre_solve = true,
			sensor_events = true,
		})
		local info = world:step(1 / 60)
		if info.commands ~= 0 then
			fail("queued command applied to recreated body")
		end
		local recreated_pose = world:pose("recreate")
		if not recreated_pose or math.abs(recreated_pose.vx) > 0.001 then
			fail("stale command mutated recreated body")
		end
		local hash_pose = world:pose("hash_body")
		if not hash_pose or hash_pose.x < -2.01 or hash_pose.x > -1.99 then
			fail("omitted body version did not hash initial state")
		end
		local explicit_pose = world:pose("explicit_body")
		if not explicit_pose or explicit_pose.x < -3.01 or explicit_pose.x > -2.99 then
			fail("explicit body version did not preserve initial state")
		end
		local versioned_joint_info = versioned_joint:info()
		if not versioned_joint_info or versioned_joint_info.collide_connected then
			fail("explicit joint version applied constructor-only collide_connected")
		end
		local versioned_aabb = versioned_shape:aabb()
		if not versioned_aabb or versioned_aabb.max_x - versioned_aabb.min_x > 0.45 then
			fail("explicit shape version recreated changed geometry")
		end
		local versioned_info = versioned_shape:info()
		if
			not versioned_info
			or not versioned_info.contact
			or not versioned_info.hit
			or not versioned_info.pre_solve
			or not versioned_info.sensor_events
		then
			fail("explicit shape version did not apply runtime event flags")
		end
		local pose = world:pose("kept")
		if not pose or pose.x < 0.99 or pose.x > 1.01 then
			fail("prune=false did not preserve undeclared body")
		end
	elseif frame == 3 then
		check_missing_world_queries()
		world:begin()
		kept_ref:set_velocity({ x = 4, y = 0 })
		local info = world:step(1 / 60)
		if info.commands ~= 0 then
			fail("queued command applied to pruned body")
		end
		local pose, err = world:pose("kept")
		if pose ~= nil or err ~= "not found" then
			fail("prune=true did not remove undeclared body")
		end
		local velocity, velocity_err = kept_ref:velocity()
		if velocity ~= nil or velocity_err ~= "not found" then
			fail("stale body velocity did not return not found")
		end
		local shapes, shapes_err = kept_ref:shapes()
		if shapes ~= nil or shapes_err ~= "not found" then
			fail("stale body shapes did not return not found")
		end
		local aabb, aabb_err = kept_shape_ref:aabb()
		if aabb ~= nil or aabb_err ~= "not found" then
			fail("stale shape aabb did not return not found")
		end
		local segments, segments_err = stale_chain_ref:segments()
		if segments ~= nil or segments_err ~= "not found" then
			fail("stale chain segments did not return not found")
		end
		local joint_info, joint_err = stale_joint_ref:info()
		if joint_info ~= nil or joint_err ~= "not found" then
			fail("stale joint info did not return not found")
		end
		local saw_contact_end = false
		for _, event in ipairs(world:contacts("end")) do
			saw_contact_end = saw_contact_end or destroyed_pair(event, "wall", "solid", "crate", "solid")
		end
		if not saw_contact_end then
			fail("destroyed contact end did not retain tombstone keys")
		end
		local saw_sensor_end = false
		for _, event in ipairs(world:sensors("end")) do
			saw_sensor_end = saw_sensor_end or destroyed_sensor(event, "sensor", "zone", "visitor", "solid")
		end
		if not saw_sensor_end then
			fail("destroyed sensor end did not retain tombstone keys")
		end
		begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
		end_pass()
		print("PHYS2D_LIFETIME_OK")
		quit()
		return
	end

	begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
	end_pass()
end

return M
