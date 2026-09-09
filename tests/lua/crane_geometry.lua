-- Compare actual drawing transforms/vertices against declared collision shapes.
-- Support planes catch wrong dimensions, reflections, pose order and missing parts.
return function(g, check)
	local shapes, draws = {}, {}
	local box, capsule = lub.phys3d.box, lub.phys3d.capsule
	lub.phys3d.box = function(_, key, d)
		shapes[key] = { kind = "box", d = d }
	end
	lub.phys3d.capsule = function(_, key, d)
		shapes[key] = { kind = "capsule", d = d }
	end
	local old_ren, old_cube, old_cylinder, old_sphere = g.ren, g.cube_mesh, g.rod_cylinder, g.rod_sphere
	g.cube_mesh = { data = Shapes3d.cube() }
	g.rod_cylinder = { data = Shapes3d.cylinder(32) }
	g.rod_sphere = { data = Shapes3d.sphere(16, 32) }
	g.ren = {
		draw = function(_, mesh, transform)
			local points, m, v = {}, transform.m, mesh.data.positions
			for i = 1, #v, 3 do
				local x, y, z = v[i], v[i + 1], v[i + 2]
				points[#points + 1] = {
					m[1] * x + m[2] * y + m[3] * z + m[4],
					m[5] * x + m[6] * y + m[7] * z + m[8],
					m[9] * x + m[10] * y + m[11] * z + m[12],
				}
			end
			draws[#draws + 1] = points
		end,
	}
	local function world(p, pose)
		local x, y, z = p.x, p.y, p.z
		local qx, qy, qz, qw = pose.qx, pose.qy, pose.qz, pose.qw
		local tx, ty, tz = 2 * (qy * z - qz * y), 2 * (qz * x - qx * z), 2 * (qx * y - qy * x)
		return {
			x + qw * tx + qy * tz - qz * ty + pose.x,
			y + qw * ty + qz * tx - qx * tz + pose.y,
			z + qw * tz + qx * ty - qy * tx + pose.z,
		}
	end
	local function dot(a, b)
		return a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
	end
	local directions = { { 1, 0, 0 }, { -1, 0, 0 }, { 0, 1, 0 }, { 0, -1, 0 }, { 0, 0, 1 }, { 0, 0, -1 } }
	for i = 1, 96 do
		local y = 1 - 2 * (i - 0.5) / 96
		local r, a = math.sqrt(1 - y * y), i * 2.3999632297
		directions[#directions + 1] = { r * math.cos(a), y, r * math.sin(a) }
	end
	local max_error, count = 0, 0
	local function compare(shape, first, last, pose, label)
		check(shape ~= nil, label .. ": missing collider")
		if not shape then
			return
		end
		local d, collider = shape.d, {}
		check(not d.filter or d.filter.mask_bits ~= "0", label .. ": collider cannot contact prizes")
		if shape.kind == "box" then
			for _, x in ipairs({ -d.hx, d.hx }) do
				for _, y in ipairs({ -d.hy, d.hy }) do
					for _, z in ipairs({ -d.hz, d.hz }) do
						collider[#collider + 1] =
							world({ x = x + d.offset.x, y = y + d.offset.y, z = z + d.offset.z }, pose)
					end
				end
			end
		else
			collider = { world(d.a, pose), world(d.b, pose) }
		end
		local error = 0
		for _, n in ipairs(directions) do
			local physical, visual = -math.huge, -math.huge
			for _, p in ipairs(collider) do
				physical = math.max(physical, dot(p, n))
			end
			if shape.kind == "capsule" then
				physical = physical + d.r
			end
			for i = first, last do
				for _, p in ipairs(draws[i] or {}) do
					visual = math.max(visual, dot(p, n))
				end
			end
			error = math.max(error, math.abs(physical - visual))
		end
		max_error, count = math.max(max_error, error), count + 1
		check(error < (shape.kind == "box" and 0.000001 or 0.0001), label .. ": visible/collision error " .. error)
	end
	local ok, err = pcall(function()
		for _, sign in ipairs({ -1, 1 }) do
			for _, angle in ipairs({ 0, 0.85, -0.65 }) do
				-- Oblique axis exercises all three rotations, including a tilted head.
				local q = math.sin(angle / 2) / math.sqrt(14)
				local pose = { x = 0.17, y = 0.43, z = -0.21, qx = q, qy = q * 2, qz = q * 3, qw = math.cos(angle / 2) }
				shapes, draws = {}, {}
				g.declare_finger_shapes({}, sign)
				g.draw_finger(pose, sign)
				check(#draws == 10, "finger must draw plate and all three capsule rods")
				compare(shapes.pad, 1, 1, pose, "plate")
				for i = 0, 2 do
					compare(shapes["rod:" .. i], 2 + i * 3, 4 + i * 3, pose, "rod:" .. i)
				end
			end
		end
	end)
	lub.phys3d.box, lub.phys3d.capsule = box, capsule
	g.ren, g.cube_mesh, g.rod_cylinder, g.rod_sphere = old_ren, old_cube, old_cylinder, old_sphere
	check(ok, tostring(err))
	print(string.format("CRANE_GEOMETRY parts=%d max_error_mm=%.5f", count, max_error * 1000))
end
