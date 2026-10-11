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

-- outer(visitor) を回し、visitor の中で inner() を呼ぶ。外側の結果の数と、
-- 内側の各呼び出しの結果の数を確かめる。keep は visitor が返す続行の値
-- (ray 系は fraction の 1、overlap / mover 系は true)。
local function nested(label, outer, inner, keep, want_outer, want_inner)
	local inner_counts = {}
	local result = outer(function()
		inner_counts[#inner_counts + 1] = #inner()
		return keep
	end)
	expect(label .. " outer", #result, want_outer)
	expect(label .. " inner calls", #inner_counts, want_outer)
	for _, n in ipairs(inner_counts) do
		expect(label .. " inner", n, want_inner)
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

	-- raycast_all と shape_cast_all は結果の buffer を共有する。
	local row_cast = { kind = "circle", radius = 0.05, x = 0, y = 0, dx = 5, dy = 0 }
	local lone_cast = { kind = "circle", radius = 0.05, x = 0, y = 5, dx = 5, dy = 0 }
	nested("2d raycast_all > shape_cast_all", function(visitor)
		return world:raycast_all(row_ray, visitor)
	end, function()
		return world:shape_cast_all(lone_cast, function()
			return 1
		end)
	end, 1, 4, 1)
	nested("2d shape_cast_all > raycast_all", function(visitor)
		return world:shape_cast_all(row_cast, visitor)
	end, function()
		return world:raycast_all(lone_ray, function()
			return 1
		end)
	end, 1, 4, 1)
	nested("2d shape_cast_all > shape_cast_all", function(visitor)
		return world:shape_cast_all(row_cast, visitor)
	end, function()
		return world:shape_cast_all(lone_cast, function()
			return 1
		end)
	end, 1, 4, 1)

	-- mover の芯が box に入ると plane の法線が決まらず拾われないので、box の
	-- 上面に少し食い込む高さに置く。
	local row_mover = { ax = 0.5, ay = 0.35, bx = 4.5, by = 0.35, r = 0.2 }
	local lone_mover = { ax = 0.8, ay = 5.35, bx = 1.2, by = 5.35, r = 0.2 }
	nested("2d collide_mover > collide_mover", function(visitor)
		return world:collide_mover(row_mover, visitor)
	end, function()
		return world:collide_mover(lone_mover, function()
			return true
		end)
	end, true, 4, 1)
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

	local function each_ray(ray)
		return function()
			return lub.phys3d.raycast_all(world, ray, function()
				return 1
			end)
		end
	end
	local function sphere_cast(y)
		return { sphere = { r = 0.05, center = { x = 0, y = y, z = 0 } }, dx = 5, dy = 0, dz = 0 }
	end
	local function each_cast(cast)
		return function()
			return lub.phys3d.shape_cast_all(world, cast, function()
				return 1
			end)
		end
	end
	-- raycast_all と shape_cast_all は結果の buffer を共有する。
	nested("3d raycast_all > shape_cast_all", function(visitor)
		return lub.phys3d.raycast_all(world, row_ray, visitor)
	end, each_cast(sphere_cast(5)), 1, 4, 1)
	nested("3d shape_cast_all > raycast_all", function(visitor)
		return lub.phys3d.shape_cast_all(world, sphere_cast(0), visitor)
	end, each_ray(lone_ray), 1, 4, 1)
	nested("3d shape_cast_all > shape_cast_all", function(visitor)
		return lub.phys3d.shape_cast_all(world, sphere_cast(0), visitor)
	end, each_cast(sphere_cast(5)), 1, 4, 1)

	-- overlap_aabb、overlap_shape、body の shapes は結果の buffer を共有する。
	local row_box = { min_x = 0.5, min_y = -0.5, min_z = -0.5, max_x = 4.5, max_y = 0.5, max_z = 0.5 }
	local lone_box = { min_x = 0.5, min_y = 4.5, min_z = -0.5, max_x = 1.5, max_y = 5.5, max_z = 0.5 }
	local row_shape = { box = { hx = 2, hy = 0.5, hz = 0.5, center = { x = 2.5, y = 0, z = 0 } } }
	local lone_shape = { sphere = { r = 0.5, center = { x = 1, y = 5, z = 0 } } }
	local function each_overlap_aabb(box)
		return function()
			return lub.phys3d.overlap_aabb(world, box, function()
				return true
			end)
		end
	end
	local function each_overlap_shape(shape)
		return function()
			return lub.phys3d.overlap_shape(world, shape, function()
				return true
			end)
		end
	end
	nested("3d overlap_aabb > overlap_aabb", function(visitor)
		return lub.phys3d.overlap_aabb(world, row_box, visitor)
	end, each_overlap_aabb(lone_box), true, 4, 1)
	nested("3d overlap_aabb > overlap_shape", function(visitor)
		return lub.phys3d.overlap_aabb(world, row_box, visitor)
	end, each_overlap_shape(lone_shape), true, 4, 1)
	nested("3d overlap_shape > overlap_aabb", function(visitor)
		return lub.phys3d.overlap_shape(world, row_shape, visitor)
	end, each_overlap_aabb(lone_box), true, 4, 1)
	nested("3d overlap_shape > overlap_shape", function(visitor)
		return lub.phys3d.overlap_shape(world, row_shape, visitor)
	end, each_overlap_shape(lone_shape), true, 4, 1)
	nested("3d overlap_shape > body shapes", function(visitor)
		return lub.phys3d.overlap_shape(world, row_shape, visitor)
	end, function()
		return lub.phys3d.body_shapes(lone)
	end, true, 4, 1)

	local row_mover = { a = { x = 0.5, y = 0.35, z = 0 }, b = { x = 4.5, y = 0.35, z = 0 }, r = 0.2 }
	local lone_mover = { a = { x = 0.8, y = 5.35, z = 0 }, b = { x = 1.2, y = 5.35, z = 0 }, r = 0.2 }
	nested("3d collide_mover > collide_mover", function(visitor)
		return lub.phys3d.collide_mover(world, row_mover, visitor)
	end, function()
		return lub.phys3d.collide_mover(world, lone_mover, function()
			return true
		end)
	end, true, 4, 1)
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
