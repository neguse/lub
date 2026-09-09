-- Run the actual transpiled sample without rendering each simulation tick.
-- CRANE_SAMPLE_LUA can point at a previous build for before/after comparisons.
local M = {}
local cases = {}

local function check(ok, message)
	if not ok then
		print("CRANE_FAIL " .. message)
		os.exit(1, true)
	end
end

local function init_cases()
	for _, name in ipairs({ "center", "turned", "miss", "open", "weak", "stock" }) do
		local game = dofile(os.getenv("CRANE_SAMPLE_LUA") or "samples/23_crane_game/.lub/CraneGame23.lua")
		game.on_init()
		if name ~= "stock" then
			game.bears = { game.bears[1] }
			local bear = game.bears[1]
			bear.x, bear.z, bear.yaw = 0, -0.08, name == "turned" and 0.8 or 0
		end
		if name == "weak" then
			game.grab_torque, game.hold_torque = 0, 0
		end
		if name == "open" then
			game.claw_command = function()
				return { speed = 1.8, torque = 0.9 }
			end
		end
		table.insert(cases, { name = name, game = game })
	end
end

local function run_cases()
	for _, case in ipairs(cases) do
		-- Generated static methods refer to this class table.
		CraneGame23 = case.game
		local game = case.game
		local world = lub.phys3d.world("crane_test:" .. case.name, {
			gravity = { x = 0, y = -9.81, z = 0 },
			fixed_dt = 1 / 60,
			substeps = 8,
			max_steps = 1,
		})
		local previous, max_step, min_carry_y, lifted, cycles = nil, 0, 1, 0, 0
		local ticks = case.name == "stock" and 12000 or 1400
		for tick = 1, ticks do
			local old_state = game.state
			game.simulate_tick(world)
			if case.name == "miss" and old_state == 0 and game.state == 1 then
				game.auto_x, game.auto_z = 0.15, -0.30
			end
			local head = lub.phys3d.pose_by_key(world, "head")
			check(head ~= nil, case.name .. ": missing head")
			check(head.y == head.y and math.abs(head.y) < 2, case.name .. ": invalid pose")
			if previous then
				local distance =
					math.sqrt((head.x - previous.x) ^ 2 + (head.y - previous.y) ^ 2 + (head.z - previous.z) ^ 2)
				max_step = math.max(max_step, distance)
			end
			previous = head
			if old_state == 6 and game.state == 7 then
				min_carry_y = math.min(min_carry_y, head.y)
			end
			if old_state == 9 and game.state == 0 then
				cycles = cycles + 1
			end
			local bear = lub.phys3d.pose_by_key(world, "bear:0")
			if game.state == 6 and bear then
				lifted = math.max(lifted, bear.y)
			end
		end
		print(
			string.format(
				"CRANE_CASE name=%s ticks=%d cycles=%d prizes=%d carry_y=%.4f head_step=%.4f lift=%.4f",
				case.name,
				ticks,
				cycles,
				game.score,
				min_carry_y,
				max_step,
				lifted
			)
		)
		check(cycles >= (case.name == "stock" and 10 or 1), case.name .. ": incomplete cycle")
		check(min_carry_y > 0.54, case.name .. ": carriage moved before head was raised")
		check(max_step < 0.025, case.name .. ": head jumped more than 25 mm in one tick")
		if case.name == "center" or case.name == "turned" then
			check(lifted > 0.20 and game.score == 1, case.name .. ": aimed prize was not delivered")
		elseif case.name == "miss" or case.name == "open" then
			check(game.score == 0, case.name .. ": unearned prize")
		elseif case.name == "stock" then
			check(game.score > 0, case.name .. ": no prizes in 200 seconds")
		end
	end
end

function M.on_init()
	local ok, err = pcall(init_cases)
	check(ok, tostring(err))
end

function M.on_frame()
	local ok, err = pcall(run_cases)
	check(ok, tostring(err))
	print("CRANE_PASS")
	os.exit(0, true)
end

return M
