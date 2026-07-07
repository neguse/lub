local M = {}

local frame = 0
local filter_calls = 0
local pre_solve_calls = 0
local friction_calls = 0
local restitution_calls = 0
local mutation_blocked = false
local saw_callback_metadata = false
local saw_event_metadata = false
local fallback_filter_errors = 0
local fallback_pre_solve_errors = 0
local fallback_friction_errors = 0
local fallback_restitution_errors = 0
local fallback_clear_checked = false

local function fail(message)
	print("PHYS2D_CALLBACKS_FAIL " .. message)
	os.exit(1, true)
end

local function pair(a, b, lhs, rhs)
	return (a.body == lhs and b.body == rhs) or (a.body == rhs and b.body == lhs)
end

local function on_filter(a, b)
	filter_calls = filter_calls + 1
	if not mutation_blocked then
		local ok, err = pcall(function()
			phys2d_world("callback_illegal", {})
		end)
		mutation_blocked = (not ok) and tostring(err):find("physics mutation") ~= nil
	end
	if pair(a, b, "ghost", "wall") then
		local ghost = a.body == "ghost" and a or b
		local wall = a.body == "wall" and a or b
		saw_callback_metadata = saw_callback_metadata
			or (
				ghost.tag == "ghost_shape"
				and ghost.material == "ghost_material"
				and ghost.user_material_id == 5
				and ghost.category == 5
				and ghost.category_bits == "0000000000000020"
				and type(ghost.mask) == "table"
				and ghost.mask[1] == 0
				and ghost.mask[#ghost.mask] == 63
				and wall.tag == "wall_shape"
				and wall.material == "wall_material"
				and wall.user_material_id == 3
				and wall.category == 0
			)
		return false
	end
	return true
end

local function on_pre_solve(contact)
	pre_solve_calls = pre_solve_calls + 1
	if type(contact.point_count) ~= "number" or type(contact.a) ~= "table" or type(contact.b) ~= "table" then
		fail("pre_solve contact missing immutable shape views")
	end
	if pair(contact.a, contact.b, "pass", "wall") then
		return false
	end
	return true
end

local function mix_friction(a, b)
	friction_calls = friction_calls + 1
	if a.material == 7 or b.material == 7 then
		return 0.25
	end
	return math.sqrt(a.friction * b.friction)
end

local function mix_restitution(a, b)
	restitution_calls = restitution_calls + 1
	if a.material == 7 or b.material == 7 then
		return 0.1
	end
	return math.max(a.restitution, b.restitution)
end

local function fallback_error_filter()
	fallback_filter_errors = fallback_filter_errors + 1
	error("filter fallback smoke")
end

local function fallback_error_pre_solve()
	fallback_pre_solve_errors = fallback_pre_solve_errors + 1
	error("pre_solve fallback smoke")
end

local function fallback_error_friction()
	fallback_friction_errors = fallback_friction_errors + 1
	error("friction fallback smoke")
end

local function fallback_error_restitution()
	fallback_restitution_errors = fallback_restitution_errors + 1
	error("restitution fallback smoke")
end

local function declare_fallback_pair(world)
	world:begin({ prune = false })
	local wall = world:body("wall", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	wall:box("solid", {
		hx = 0.25,
		hy = 0.25,
		contact = true,
		pre_solve = true,
	})
	local box = world:body("box", {
		type = DYNAMIC,
		initial = { x = 0, y = 0 },
	})
	box:box("solid", {
		hx = 0.2,
		hy = 0.2,
		density = 1,
		contact = true,
		pre_solve = true,
	})
	world:step(1 / 60)
end

local function run_fallback_smoke()
	if frame == 1 then
		local world = phys2d_world("callback_fallback", {
			gravity = { x = 0, y = 0 },
			fixed_dt = 1 / 60,
			substeps = 4,
			max_steps = 1,
			sleep = false,
			callbacks = {
				filter = fallback_error_filter,
				pre_solve = fallback_error_pre_solve,
				friction = fallback_error_friction,
				restitution = fallback_error_restitution,
			},
		})
		declare_fallback_pair(world)
		if
			fallback_filter_errors < 1
			or fallback_pre_solve_errors < 1
			or fallback_friction_errors < 1
			or fallback_restitution_errors < 1
		then
			fail("callback fallback errors were not exercised")
		end
	elseif frame == 2 then
		local before = fallback_filter_errors
			+ fallback_pre_solve_errors
			+ fallback_friction_errors
			+ fallback_restitution_errors
		local world = phys2d_world("callback_fallback", {
			gravity = { x = 0, y = 0 },
			fixed_dt = 1 / 60,
			substeps = 4,
			max_steps = 1,
			sleep = false,
		})
		declare_fallback_pair(world)
		local after = fallback_filter_errors
			+ fallback_pre_solve_errors
			+ fallback_friction_errors
			+ fallback_restitution_errors
		if after ~= before then
			fail("omitted callbacks did not clear previous callback refs")
		end
		fallback_clear_checked = true
	end
end

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

function M.onFrame()
	frame = frame + 1
	run_fallback_smoke()

	local world = phys2d_world("callbacks", {
		gravity = { x = 0, y = 0 },
		fixed_dt = 1 / 60,
		substeps = 4,
		max_steps = 1,
		sleep = false,
		callbacks = {
			filter = on_filter,
			pre_solve = on_pre_solve,
			friction = mix_friction,
			restitution = mix_restitution,
		},
	})
	world:begin()

	local wall = world:body("wall", {
		type = STATIC,
		initial = { x = 0, y = 0 },
	})
	wall:box("solid", {
		hx = 0.08,
		hy = 2.0,
		tag = "wall_shape",
		friction = 0.8,
		restitution = 0.0,
		material = "wall_material",
		user_material_id = 3,
		contact = true,
		pre_solve = true,
	})

	local ghost = world:body("ghost", {
		type = DYNAMIC,
		fixed_rotation = true,
		initial = { x = -1.0, y = 1.2, vx = 3.0, vy = 0 },
	})
	ghost:box("solid", {
		hx = 0.08,
		hy = 0.08,
		tag = "ghost_shape",
		density = 1,
		material = "ghost_material",
		user_material_id = 5,
		pre_solve = true,
		filter = { category = 5, mask = "all" },
	})

	local pass = world:body("pass", {
		type = DYNAMIC,
		fixed_rotation = true,
		initial = { x = -1.0, y = 0.0, vx = 3.0, vy = 0 },
	})
	pass:box("solid", {
		hx = 0.08,
		hy = 0.08,
		tag = "pass_shape",
		density = 1,
		material = "pass_material",
		user_material_id = 6,
		pre_solve = true,
	})

	local mix = world:body("mix", {
		type = DYNAMIC,
		fixed_rotation = true,
		initial = { x = -1.0, y = -1.2, vx = 3.0, vy = 0 },
	})
	mix:box("solid", {
		hx = 0.08,
		hy = 0.08,
		tag = "mix_shape",
		density = 1,
		friction = 0.2,
		restitution = 0.3,
		material = "mix_material",
		user_material_id = 7,
		contact = true,
		pre_solve = true,
	})

	world:step(1 / 60)

	for _, event in ipairs(world:contacts("begin")) do
		if pair(event.a, event.b, "mix", "wall") then
			local mix_shape = event.a.body == "mix" and event.a or event.b
			saw_event_metadata = saw_event_metadata
				or (
					mix_shape.tag == "mix_shape"
					and mix_shape.material == "mix_material"
					and mix_shape.user_material_id == 7
				)
		end
	end

	if frame < 48 then
		begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
		end_pass()
		return
	end

	local ghost_pose = ghost:pose()
	local pass_pose = pass:pose()
	if filter_calls < 1 then
		fail("custom filter did not run")
	end
	if pre_solve_calls < 1 then
		fail("pre_solve did not run")
	end
	if friction_calls < 1 then
		fail("friction mixer did not run")
	end
	if restitution_calls < 1 then
		fail("restitution mixer did not run")
	end
	if not mutation_blocked then
		fail("callback mutation guard did not trigger")
	end
	if not saw_callback_metadata then
		fail("callback shape metadata was not exposed")
	end
	if not saw_event_metadata then
		fail("event shape metadata was not copied")
	end
	if not fallback_clear_checked then
		fail("callback clearing smoke did not run")
	end
	local overlaps = world:overlap_aabb({
		min_x = -0.1,
		min_y = -2.0,
		max_x = 0.1,
		max_y = 2.0,
	})
	local saw_query_metadata = false
	for _, hit in ipairs(overlaps) do
		if
			hit.body == "wall"
			and hit.tag == "wall_shape"
			and hit.material == "wall_material"
			and hit.user_material_id == 3
		then
			saw_query_metadata = true
		end
	end
	if not saw_query_metadata then
		fail("query shape metadata was not exposed")
	end
	if ghost_pose.x <= 0.2 then
		fail("custom filter did not let ghost pass wall: x=" .. tostring(ghost_pose.x))
	end
	if pass_pose.x <= 0.2 then
		fail("pre_solve did not disable wall solve: x=" .. tostring(pass_pose.x))
	end

	begin_pass({ target = main_tex, clear_color = { 0.015, 0.015, 0.02, 1.0 } })
	end_pass()
	print(
		"PHYS2D_CALLBACKS_OK filter="
			.. filter_calls
			.. " pre_solve="
			.. pre_solve_calls
			.. " friction="
			.. friction_calls
			.. " restitution="
			.. restitution_calls
			.. " ghost_x="
			.. string.format("%.4f", ghost_pose.x)
			.. " pass_x="
			.. string.format("%.4f", pass_pose.x)
	)
	quit()
end

return M
