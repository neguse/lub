-- Test the physics/render boundary without changing the existing Mat4 convention.
local x = dofile("samples/lubx.lua")
local V, Q, M, R = x.Vec3, x.Quat, x.Mat4, x.Renderer3d
local function near(a, b, label)
	assert(math.abs(a.x - b.x) < 1e-5 and math.abs(a.y - b.y) < 1e-5 and math.abs(a.z - b.z) < 1e-5, label)
end
return {
	on_init = function()
		lub.config({ width = 320, height = 180 })
	end,
	on_frame = function()
		local ok, err = pcall(function()
			-- Existing left-handed angle constructors and quaternion-to-matrix behavior.
			local axes = { V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1) }
			near(M.rotate_z(math.pi / 2):mul_dir(axes[1]), V.new(0, -1, 0), "Mat4 angle rule changed")
			local p = V.new(0.7, -0.4, 0.3)
			for i, rotate in ipairs({ M.rotate_x, M.rotate_y, M.rotate_z }) do
				near(
					rotate(0.7):mul_dir(p),
					Q.from_axis_angle(axes[i], 0.7):to_mat4():mul_dir(p),
					"existing ToMat4 rule changed"
				)
				near(M.rotate(0.7, axes[i]):mul_dir(p), rotate(0.7):mul_dir(p), "arbitrary axis rule changed")
			end
			axes[4] = V.new(1, 2, 3):normalize()
			for i, axis in ipairs(axes) do
				for j, angle in ipairs({ math.pi / 2, -0.7, 0 }) do
					-- Rodrigues formula, independent of both PoseMat and ToMat4.
					local c, s = math.cos(angle), math.sin(angle)
					local dot = axis.x * p.x + axis.y * p.y + axis.z * p.z
					local expected = V.new(
						2 + p.x * c + (axis.y * p.z - axis.z * p.y) * s + axis.x * dot * (1 - c),
						3 + p.y * c + (axis.z * p.x - axis.x * p.z) * s + axis.y * dot * (1 - c),
						4 + p.z * c + (axis.x * p.y - axis.y * p.x) * s + axis.z * dot * (1 - c)
					)
					local q = Q.from_axis_angle(axis, angle)
					local w = lub.phys3d.world("pose_boundary:" .. i .. ":" .. j, { gravity = { x = 0, y = 0, z = 0 } })
					w:begin()
					local b = lub.phys3d.body(w, "body", {
						type = lub.phys3d.STATIC,
						initial = { x = 2, y = 3, z = 4, quat = { x = q.x, y = q.y, z = q.z, w = q.w } },
					})
					lub.phys3d.sphere(b, "offset", { r = 0.05, offset = { x = p.x, y = p.y, z = p.z } })
					w:step(1 / 60)
					local pose = lub.phys3d.pose(b)
					near(R.pose_mat(pose):mul_point(p), expected, "rendered point differs from physics rotation")
					local hit = lub.phys3d.raycast(
						w,
						{ x = expected.x, y = expected.y, z = expected.z - 0.5, dx = 0, dy = 0, dz = 1 }
					)
					assert(hit, "expected point is not at the real collider")
				end
			end
		end)
		print(ok and "PHYS3D_POSE_MATRIX_PASS cases=12" or "PHYS3D_POSE_MATRIX_FAIL " .. tostring(err))
		os.exit(ok and 0 or 1, true)
	end,
}
