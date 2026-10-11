-- visitor の中から同じ (か結果の buffer を共有する) query を呼んでも、外側の
-- 結果が内側の結果で上書きされない。

local function fail(message)
	print("PHYS_NESTED_QUERY_FAIL " .. message)
	os.exit(1, true)
end

local function expect(label, got, want)
	if got ~= want then
		fail(label .. ": got " .. tostring(got) .. ", want " .. tostring(want))
	end
end

-- y = 0 の列に 4 個、y = 5 に 1 個の box。外側の query は y = 0 の列を、
-- 内側の query は y = 5 の 1 個を拾う。
local function check_2d()
	local world = lub.phys2d.world("nested2d", { gravity = { x = 0, y = 0 } })
	world:begin()
	for i = 1, 4 do
		world:body("row" .. i, { type = lub.phys2d.STATIC, initial = { x = i, y = 0 } }):box("box", { hx = 0.2, hy = 0.2 })
	end
	local lone = world:body("lone", { type = lub.phys2d.STATIC, initial = { x = 1, y = 5 } })
	lone:box("box", { hx = 0.2, hy = 0.2 })
	world:step(1 / 60)

	local row_ray = { x = 0, y = 0, dx = 5, dy = 0 }
	local lone_ray = { x = 0, y = 5, dx = 5, dy = 0 }
	local inner_counts = {}
	local outer = world:raycast_all(row_ray, function()
		local inner = world:raycast_all(lone_ray, function()
			return 1
		end)
		inner_counts[#inner_counts + 1] = #inner
		return 1
	end)
	expect("2d raycast_all outer", #outer, 4)
	expect("2d raycast_all inner calls", #inner_counts, 4)
	for _, n in ipairs(inner_counts) do
		expect("2d raycast_all inner", n, 1)
	end

	local row_box = { min_x = 0.5, min_y = -0.5, max_x = 4.5, max_y = 0.5 }
	local lone_box = { min_x = 0.5, min_y = 4.5, max_x = 1.5, max_y = 5.5 }
	local inner_overlaps, inner_shapes = 0, 0
	local overlaps = world:overlap_aabb(row_box, function()
		inner_overlaps = inner_overlaps + #world:overlap_aabb(lone_box, function()
			return true
		end)
		inner_shapes = inner_shapes + #lone:shapes()
		return true
	end)
	expect("2d overlap_aabb outer", #overlaps, 4)
	expect("2d overlap_aabb inner", inner_overlaps, 4)
	expect("2d body shapes inside overlap_aabb", inner_shapes, 4)
end

local function check_3d()
	local world = lub.phys3d.world("nested3d", { gravity = { x = 0, y = 0, z = 0 } })
	world:begin()
	for i = 1, 4 do
		local body = lub.phys3d.body(world, "row" .. i, { type = lub.phys3d.STATIC, initial = { x = i, y = 0, z = 0 } })
		lub.phys3d.box(body, "box", { hx = 0.2, hy = 0.2, hz = 0.2 })
	end
	local lone = lub.phys3d.body(world, "lone", { type = lub.phys3d.STATIC, initial = { x = 1, y = 5, z = 0 } })
	lub.phys3d.box(lone, "box", { hx = 0.2, hy = 0.2, hz = 0.2 })
	world:step(1 / 60)

	local row_ray = { x = 0, y = 0, z = 0, dx = 5, dy = 0, dz = 0 }
	local lone_ray = { x = 0, y = 5, z = 0, dx = 5, dy = 0, dz = 0 }
	local inner_total = 0
	local outer = lub.phys3d.raycast_all(world, row_ray, function()
		inner_total = inner_total + #lub.phys3d.raycast_all(world, lone_ray, function()
			return 1
		end)
		return 1
	end)
	expect("3d raycast_all outer", #outer, 4)
	expect("3d raycast_all inner", inner_total, 4)
end

return {
	on_init = function()
		lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	end,
	on_frame = function()
		check_2d()
		check_3d()
		print("PHYS_NESTED_QUERY_PASS")
		os.exit(0, true)
	end,
}
