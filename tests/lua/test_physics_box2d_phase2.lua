local M = {}

local frame = 0
local saw_sensor = false
local saw_body_move = false
local checked_queries = false

local function fail(message)
	print("PHYS2D_PHASE2_FAIL " .. message)
	os.exit(1, true)
end

local function has_shape(items, body, shape)
	for _, item in ipairs(items) do
		if item.body == body and item.shape == shape and item.valid then
			return true
		end
	end
	return false
end

local function near(a, b)
	return math.abs(a - b) < 0.001
end

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sokol", width = 320, height = 180 })
end

function M.onFrame()
	frame = frame + 1

	local world = phys2d_world("phase2", {
		gravity = { x = 0, y = 0 },
		fixedDt = 1 / 60,
		substeps = 4,
		maxSteps = 1,
	})
	if frame == 1 then
		local no_begin_info = world:step(0)
		if no_begin_info.steps ~= 0 or no_begin_info.commands ~= 0 then
			fail("step without begin did not return empty StepInfo")
		end
	end
	world:begin()
	local world_info = world:info()
	if
		not world_info.valid
		or world_info.key ~= "phase2"
		or not world_info.begun
		or not world_info.prune
		or not near(world_info.fixed_dt, 1 / 60)
		or world_info.substeps ~= 4
		or world_info.max_steps ~= 1
		or not world_info.gravity
		or not near(world_info.gravity.x, 0)
		or not near(world_info.gravity.y, 0)
		or not world_info.continuous
		or not world_info.sleep
	then
		fail("world info did not round-trip declaration")
	end

	local ground = world:body("ground", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	local floor_shape = ground:segment("floor", {
		ax = -3,
		ay = -0.5,
		bx = 3,
		by = -0.5,
		filter = { category = 0, mask = "all" },
	})

	local sensor = world:body("sensor", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	local sensor_shape = sensor:box("zone", {
		hx = 0.3,
		hy = 0.35,
		sensor = true,
		sensorEvents = true,
		filter = { category = 2, mask = "all" },
	})

	local rock = world:body("rock", {
		type = STATIC,
		initial = { x = 2, y = 0 },
	})
	local rock_shape = rock:polygon("hull", {
		points = {
			{ x = -0.3, y = -0.2 },
			{ x = 0.35, y = -0.15 },
			{ x = 0.2, y = 0.25 },
			{ x = -0.25, y = 0.3 },
		},
		radius = 0.01,
		filter = { category = 3, mask = "all" },
	})

	local terrain = world:body("terrain", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	local terrain_chain = terrain:chain("path", {
		version = 1,
		points = {
			{ x = -3, y = -0.8 },
			{ x = -1, y = -0.8 },
			{ x = 1, y = -0.8 },
			{ x = 3, y = -0.8 },
		},
		material = "terrain",
		materials = {
			{ material = 9, friction = 0.7 },
			{ material = 9, friction = 0.7 },
			{ material = 9, friction = 0.7 },
			{ material = 9, friction = 0.7 },
		},
		friction = 0.7,
		filter = { category = 4, mask = "all" },
	})

	local mover = world:body("mover", {
		type = DYNAMIC,
		fixedRotation = true,
		initial = { x = -1, y = 0 },
	})
	local sleepy = world:body("sleepy", {
		type = DYNAMIC,
		awake = false,
		initial = { x = -2.5, y = 1.0 },
	})
	sleepy:box("solid", {
		hx = 0.1,
		hy = 0.1,
		density = 1,
	})
	local disabled = world:body("disabled", {
		type = DYNAMIC,
		enabled = false,
		initial = { x = -2.5, y = 1.5 },
	})
	disabled:box("solid", {
		hx = 0.1,
		hy = 0.1,
		density = 1,
	})
	local nosleep = world:body("nosleep", {
		type = DYNAMIC,
		sleep = false,
		sleep_threshold = 0.2,
		initial = { x = -2.8, y = 2.0 },
	})
	nosleep:box("solid", {
		hx = 0.1,
		hy = 0.1,
		density = 1,
	})
	local mover_shape = mover:capsule("capsule", {
		ax = -0.15,
		ay = 0,
		bx = 0.15,
		by = 0,
		r = 0.1,
		density = 1,
		tag = "player",
		material = "player",
		userMaterialId = 7,
		contact = true,
		hit = true,
		sensorEvents = true,
		preSolve = true,
		filter = { category = 1, mask = "all" },
	})
	local pre_command_velocity = mover:velocity()
	mover:add_impulse_center({ x = 0.001, y = 0 })
	mover:set_velocity({ x = 2.0, y = 0 })
	mover:set_mass_data({
		mass = 2.0,
		inertia = 1.0,
		center = { x = 0.05, y = 0 },
	})
	local queued_velocity = mover:velocity()
	if not near(queued_velocity.x, pre_command_velocity.x) or not near(queued_velocity.y, pre_command_velocity.y) then
		fail("queued velocity command applied before step")
	end
	local queued_info = world:info()
	if queued_info.pending_commands ~= 3 then
		fail("unexpected pending command count: " .. tostring(queued_info.pending_commands))
	end

	local info = world:step(1 / 60)
	if info.steps ~= 1 then
		fail("unexpected step count: " .. tostring(info.steps))
	end
	if info.commands ~= 3 then
		fail("unexpected applied command count: " .. tostring(info.commands))
	end
	local sleepy_pose = sleepy:pose()
	if sleepy_pose.awake ~= false then
		fail("body awake runtime declaration did not apply")
	end
	local disabled_pose = disabled:pose()
	if disabled_pose.enabled ~= false then
		fail("body enabled runtime declaration did not apply")
	end
	local nosleep_pose = nosleep:pose()
	if nosleep_pose.sleep ~= false or not near(nosleep_pose.sleep_threshold, 0.2) then
		fail("body sleep runtime declaration did not apply")
	end
	if info.body_events ~= info.body_moves then
		fail("StepInfo body_events alias mismatch")
	end
	if
		type(info.contact_ends) ~= "number"
		or type(info.contact_hits) ~= "number"
		or type(info.sensor_ends) ~= "number"
	then
		fail("StepInfo event counters missing")
	end
	local chain_segments = terrain_chain:segments()
	if #chain_segments ~= 1 or chain_segments[1].chain ~= "path" or chain_segments[1].kind ~= "chain_segment" then
		fail("chain segment enumeration missing terrain segment")
	end
	if chain_segments[1].material ~= "terrain" or chain_segments[1].user_material_id ~= 9 then
		fail(
			"chain segment metadata/material missing: material="
				.. tostring(chain_segments[1].material)
				.. " user_material_id="
				.. tostring(chain_segments[1].user_material_id)
		)
	end
	local terrain_shapes = terrain:shapes()
	if #terrain_shapes ~= 1 or terrain_shapes[1].kind ~= "chain_segment" then
		fail("body shape enumeration missing chain segment")
	end
	local mover_shapes = mover:shapes()
	if #mover_shapes ~= 1 or mover_shapes[1].shape ~= "capsule" or mover_shapes[1].kind ~= "capsule" then
		fail("body shape enumeration missing mover capsule")
	end
	local mover_info = mover_shape:info()
	local floor_info = floor_shape:info()
	if not floor_info or not near(floor_info.density, 0) then
		fail("static shape default density was not zero")
	end
	if
		not mover_info.valid
		or mover_info.body ~= "mover"
		or mover_info.shape ~= "capsule"
		or mover_info.kind ~= "capsule"
		or mover_info.tag ~= "player"
		or mover_info.material ~= "player"
		or mover_info.user_material_id ~= 7
	then
		fail("shape info identity/material metadata missing")
	end
	if
		not near(mover_info.density, 1)
		or mover_info.sensor
		or not mover_info.contact
		or not mover_info.hit
		or not mover_info.sensor_events
		or not mover_info.pre_solve
	then
		fail("shape info physical/event flags missing")
	end
	if
		not mover_info.filter
		or mover_info.filter.category_bits ~= "0000000000000002"
		or mover_info.filter.mask_bits ~= "ffffffffffffffff"
		or mover_info.filter.group ~= 0
	then
		fail("shape info filter missing")
	end
	if not mover_info.aabb or mover_info.aabb.min_x >= mover_info.aabb.max_x then
		fail("shape info aabb missing")
	end
	if not sensor_shape:test_point({ x = 0, y = 0 }) then
		fail("shape test_point missed sensor center")
	end
	if not rock_shape:test_point({ x = 2, y = 0 }) then
		fail("polygon test_point missed rock center")
	end
	local sensor_aabb = sensor_shape:aabb()
	if sensor_aabb.min_x > -0.29 or sensor_aabb.max_x < 0.29 then
		fail("shape aabb missing sensor extents")
	end
	local closest = sensor_shape:closest_point({ x = 1, y = 0 })
	if closest.x < 0.25 or closest.x > 0.36 then
		fail("shape closest_point unexpected x=" .. tostring(closest.x))
	end
	local shape_hit = floor_shape:raycast({ x = 0, y = 1, dx = 0, dy = -2 })
	if not shape_hit or shape_hit.y > -0.49 or shape_hit.y < -0.51 then
		fail("shape raycast missed floor")
	end
	local velocity = mover:velocity()
	if velocity.x <= 0 then
		fail("velocity readback did not move right")
	end
	local mass = mover:mass()
	if type(mass.mass) ~= "number" or not near(mass.mass, 2.0) then
		fail("mass readback missing")
	end
	if not near(mass.inertia, 1.0) or not near(mass.local_center.x, 0.05) then
		fail("mass data override did not round-trip")
	end
	local center = mover:center()
	if type(center.x) ~= "number" or type(center.y) ~= "number" then
		fail("center readback missing")
	end
	local world_origin = mover:world_point({ x = 0, y = 0 })
	local local_origin = mover:local_point(world_origin)
	if not near(local_origin.x, 0) or not near(local_origin.y, 0) then
		fail("point conversion did not round-trip")
	end
	local velocity_at_center = mover:velocity_at(center)
	if velocity_at_center.x <= 0 then
		fail("point velocity readback missing")
	end

	for _, event in ipairs(world:body_events()) do
		if event.body == "mover" and event.valid then
			saw_body_move = true
		end
	end

	for _, event in ipairs(world:sensors("begin")) do
		if event.sensor.body == "sensor" and event.sensor.shape == "zone" and event.visitor.body == "mover" then
			saw_sensor = true
		end
	end

	if not checked_queries then
		local hit = world:raycast({
			x = 0,
			y = 1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		})
		if not hit or hit.body ~= "ground" or hit.shape ~= "floor" then
			fail("closest raycast missed ground")
		end

		local visited = false
		local hits = world:raycast({
			x = 0,
			y = 1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		}, function(visitor_hit)
			visited = visited or visitor_hit.body == "ground"
			return "clip"
		end)
		if not visited or #hits < 1 then
			fail("visitor raycast missed ground")
		end
		local bad_raycast, bad_raycast_err = world:raycast({
			x = 0,
			y = 1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		}, function()
			error("raycast visitor boom")
		end)
		if
			bad_raycast ~= nil
			or type(bad_raycast_err) ~= "string"
			or not bad_raycast_err:find("phys2d_raycast visitor")
		then
			fail("raycast visitor error did not return nil,error")
		end
		local mutating_raycast, mutating_raycast_err = world:raycast({
			x = 0,
			y = 1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		}, function()
			mover:set_velocity({ x = 0, y = 0 })
			return "clip"
		end)
		if
			mutating_raycast ~= nil
			or type(mutating_raycast_err) ~= "string"
			or not mutating_raycast_err:find("physics mutation")
		then
			fail("raycast visitor mutation was not rejected")
		end

		local overlaps = world:overlap_aabb({
			min_x = -0.4,
			min_y = -0.4,
			max_x = 0.4,
			max_y = 0.4,
			filter = { mask = { 2 } },
		})
		if not has_shape(overlaps, "sensor", "zone") then
			fail("overlap_aabb missed sensor zone")
		end
		local bad_overlap, bad_overlap_err = world:overlap_aabb({
			min_x = -0.4,
			min_y = -0.4,
			max_x = 0.4,
			max_y = 0.4,
			filter = { mask = { 2 } },
		}, function()
			error("overlap visitor boom")
		end)
		if
			bad_overlap ~= nil
			or type(bad_overlap_err) ~= "string"
			or not bad_overlap_err:find("phys2d_overlap_aabb visitor")
		then
			fail("overlap_aabb visitor error did not return nil,error")
		end
		local cast = world:shape_cast({
			type = "circle",
			x = 0,
			y = 1,
			r = 0.1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		})
		if not cast or cast.body ~= "ground" or cast.shape ~= "floor" then
			fail("closest shape_cast missed ground")
		end
		local shape_cast_visited = false
		local shape_cast_hits = world:shape_cast({
			type = "box",
			x = 0,
			y = 1,
			hx = 0.1,
			hy = 0.1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		}, function(hit)
			shape_cast_visited = shape_cast_visited or hit.body == "ground"
			return "clip"
		end)
		if not shape_cast_visited or #shape_cast_hits < 1 then
			fail("visitor shape_cast missed ground")
		end
		local bad_shape_cast, bad_shape_cast_err = world:shape_cast({
			type = "box",
			x = 0,
			y = 1,
			hx = 0.1,
			hy = 0.1,
			dx = 0,
			dy = -2,
			filter = { mask = { 0 } },
		}, function()
			error("shape_cast visitor boom")
		end)
		if
			bad_shape_cast ~= nil
			or type(bad_shape_cast_err) ~= "string"
			or not bad_shape_cast_err:find("phys2d_shape_cast visitor")
		then
			fail("shape_cast visitor error did not return nil,error")
		end
		checked_queries = true
	end

	begin_pass({ target = main_tex, clear_color = { 0.02, 0.02, 0.025, 1.0 } })
	end_pass()

	if saw_sensor and saw_body_move and checked_queries then
		mover_shape:set_material({
			material = "runtime",
			user_material_id = 11,
			friction = 0.25,
			restitution = 0.4,
			density = 1.25,
		})
		mover_shape:set_filter({ category = 5, mask = { 5 } })
		mover_shape:set_events({
			contact = false,
			hit = false,
			sensor_events = false,
			pre_solve = false,
		})
		local runtime_info = mover_shape:info()
		if
			runtime_info.material ~= "runtime"
			or runtime_info.user_material_id ~= 11
			or not near(runtime_info.friction, 0.25)
			or not near(runtime_info.restitution, 0.4)
			or not near(runtime_info.density, 1.25)
		then
			fail("shape runtime material setter did not round-trip")
		end
		if
			runtime_info.filter.category_bits ~= "0000000000000020"
			or runtime_info.filter.mask_bits ~= "0000000000000020"
		then
			fail("shape runtime filter setter did not round-trip")
		end
		if runtime_info.contact or runtime_info.hit or runtime_info.sensor_events or runtime_info.pre_solve then
			fail("shape runtime event setter did not round-trip")
		end
		local pose = mover:pose()
		print("PHYS2D_PHASE2_OK frame=" .. frame .. " x=" .. string.format("%.4f", pose.x))
		quit()
		return
	end

	if frame > 120 then
		local pose = mover:pose()
		fail(
			"sensor/body events not observed"
				.. " sensor="
				.. tostring(saw_sensor)
				.. " body_move="
				.. tostring(saw_body_move)
				.. " x="
				.. string.format("%.4f", pose.x)
		)
	end
end

return M
