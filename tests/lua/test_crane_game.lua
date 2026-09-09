-- Replay the actual transpiled sample, without real-time input or per-tick drawing.
local M = {}
local cases, bodies, joints, names = {}, {}, {}, {}
local sample = os.getenv("CRANE_SAMPLE_LUA") or "samples/23_crane_game/.lub/CraneGame23.lua"
local failures = {}
local function check(ok, message)
	if not ok then
		failures[#failures + 1] = message
	end
end
local function fixture(name, yaw, offset)
	local g = dofile(sample)
	g.on_init()
	if name ~= "stock" then
		g.bears = { g.bears[1] }
		local b = g.bears[1]
		b.x, b.z, b.yaw = 0, -0.08, yaw or 0
	end
	if name == "unpowered" then
		g.grab_force, g.hold_force = 0, 0
	end
	if name == "power_cut" then
		g.hold_force = 0
	end
	cases[#cases + 1] = { name = name, game = g, yaw = yaw or 0, offset = offset or 0 }
end
function M.on_init()
	local ok, err = pcall(function()
		for yaw = 0, 15 do
			for _, offset in ipairs({ 0, 0.02, -0.02 }) do
				fixture("grid", yaw * 0.4, offset)
			end
		end
		for _, name in ipairs({ "miss", "unpowered", "power_cut", "stock" }) do
			fixture(name)
		end
	end)
	if not ok then
		print("CRANE_FAIL " .. tostring(err))
		os.exit(1, true)
	end
end

local function support(body)
	local left, right, floor = false, false, false
	for _, c in ipairs(lub.phys3d.body_contacts(body)) do
		if c.point_count > 0 and (c.separation or 0) <= 0.002 then
			local other = c.a.body == "bear:0" and c.b.body or c.a.body
			left = left or other == "finger:l"
			right = right or other == "finger:r"
			floor = floor or other:sub(1, 7) == "static:"
		end
	end
	return left, right, floor
end

local function run_cases()
	CraneGame23 = cases[1].game
	dofile("tests/lua/crane_geometry.lua")(CraneGame23, check)
	-- Observe declarations and commands; never alter simulation inputs here.
	local body, joint = lub.phys3d.body, lub.phys3d.joint
	lub.phys3d.body = function(w, k, d)
		local b = body(w, k, d)
		bodies[k] = b
		names[b] = k
		return b
	end
	lub.phys3d.joint = function(w, k, d)
		check(not (names[d.body_a] or ""):find("bear:", 1, true), "joint attached to prize")
		check(not (names[d.body_b] or ""):find("bear:", 1, true), "joint attached to prize")
		local j = joint(w, k, d)
		joints[k] = j
		return j
	end
	for _, command in ipairs({
		"add_force",
		"add_force_center",
		"add_impulse",
		"add_impulse_center",
		"set_target",
		"set_pose",
		"set_velocity",
	}) do
		local original = lub.phys3d[command]
		if original then
			lub.phys3d[command] = function(b, ...)
				check(not (names[b] or ""):find("bear:", 1, true), command .. " used on prize")
				return original(b, ...)
			end
		end
	end
	local deliveries, grips, centered = 0, 0, 0
	for i, case in ipairs(cases) do
		CraneGame23 = case.game
		local g = case.game
		bodies, joints, names = {}, {}, {}
		local w = lub.phys3d.world("crane_replay:" .. i, {
			gravity = { x = 0, y = -9.81, z = 0 },
			fixed_dt = 1 / 60,
			substeps = g.physics_substeps or 8,
			max_steps = 1,
		})
		local previous, max_step, min_carry_y, cycles = nil, 0, 1, 0
		local bilateral, carried, grounded_after_cut, max_link_error = 0, 0, false, 0
		local ticks = case.name == "stock" and 12000 or 1400
		for tick = 1, ticks do
			local old = g.state
			g.simulate_tick(w)
			if old == 0 and g.state == 1 then
				g.auto_x, g.auto_z = g.auto_x + case.offset, g.auto_z + case.offset
				if case.name == "miss" then
					g.auto_x, g.auto_z = 0.15, -0.30
				end
			end
			local head = lub.phys3d.pose(bodies.head)
			assert(head and head.y == head.y and math.abs(head.y) < 2, "invalid head pose")
			if previous then
				max_step = math.max(
					max_step,
					math.sqrt((head.x - previous.x) ^ 2 + (head.y - previous.y) ^ 2 + (head.z - previous.z) ^ 2)
				)
			end
			previous = head
			if old == 6 and g.state == 7 then
				min_carry_y = math.min(min_carry_y, head.y)
			end
			if old == 9 and g.state == 0 then
				cycles = cycles + 1
			end
			if joints["link:r"] then
				max_link_error = math.max(
					max_link_error,
					math.abs(lub.phys3d.joint_info(joints["link:r"]).linear_separation),
					math.abs(lub.phys3d.joint_info(joints["link:l"]).linear_separation)
				)
			end
			local bear = lub.phys3d.pose_by_key(w, "bear:0")
			if bear and (g.state == 6 or g.state == 7) then
				local left, right, floor = support(bodies["bear:0"])
				if not floor and left and right then
					bilateral = bilateral + 1
				end
				if g.state == 7 and not floor and (left or right) then
					carried = carried + 1
				end
				if case.name == "power_cut" and g.state == 7 and floor then
					grounded_after_cut = true
				end
			end
		end
		print(
			string.format(
				"CRANE_CASE name=%s yaw=%.1f offset=%.2f prizes=%d cycles=%d bilateral=%d carry_contact=%d carry_y=%.4f head_step=%.4f link_error=%.4f",
				case.name,
				case.yaw,
				case.offset,
				g.score,
				cycles,
				bilateral,
				carried,
				min_carry_y,
				max_step,
				max_link_error
			)
		)
		check(cycles >= (case.name == "stock" and 10 or 1), case.name .. ": incomplete cycle")
		check(min_carry_y > 0.54, case.name .. ": carrying before head rose")
		check(max_step < 0.025, case.name .. ": head jumped over 25 mm/tick")
		check(max_link_error < 0.005, case.name .. ": linkage stretched over 5 mm")
		if case.name == "grid" then
			if g.score > 0 then
				deliveries = deliveries + 1
				if case.offset == 0 then
					centered = centered + 1
				end
				check(bilateral >= 10, "delivery without bilateral airborne grip, yaw=" .. case.yaw)
			end
			if carried >= 90 then
				grips = grips + 1
			end
		elseif case.name == "stock" then
			check(g.score >= 4, "stock: fewer than four prizes in 200 seconds")
		else
			check(g.score == 0, case.name .. ": unearned prize")
			if case.name == "power_cut" then
				check(bilateral >= 10 and grounded_after_cut, "power cut must drop a previously gripped prize")
			end
		end
	end
	print(
		string.format(
			"CRANE_GRID cases=48 deliveries=%d sustained_grips=%d centered_deliveries=%d/16",
			deliveries,
			grips,
			centered
		)
	)
	check(deliveries >= 36 and grips >= 36 and centered >= 13, "insufficient grip coverage")
end
function M.on_frame()
	local ok, err = pcall(run_cases)
	check(ok, tostring(err))
	for _, failure in ipairs(failures) do
		print("CRANE_FAIL " .. failure)
	end
	if #failures > 0 then
		os.exit(1, true)
	end
	print("CRANE_PASS")
	os.exit(0, true)
end
return M
