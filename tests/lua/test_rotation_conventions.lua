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
			local axes = { V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1), V.new(1, 2, 3) }
			local p = V.new(0.7, -0.4, 0.3)
			for _, axis in ipairs(axes) do
				for _, angle in ipairs({ 0, 0.7, -1.2, math.pi / 2 }) do
					local q = Q.from_axis_angle(axis, angle)
					near(q:to_mat4():mul_dir(p), q:rotate_vec3(p), "matrix/quaternion disagree")
					near(M.from_quat(q):mul_dir(p), q:rotate_vec3(p), "FromQuat disagrees")
					near(
						M.rotate(angle, axis):mul_dir(p),
						Q.from_axis_angle(axis, -angle):rotate_vec3(p),
						"legacy angle changed"
					)
				end
			end
			local a, b = Q.from_axis_angle(axes[1], 0.7), Q.from_axis_angle(axes[2], -1.2)
			near(a:mul(b):to_mat4():mul_dir(p), a:to_mat4():mul(b:to_mat4()):mul_dir(p), "composition order reversed")
			for i, rotate in ipairs({ M.rotate_x, M.rotate_y, M.rotate_z }) do
				near(rotate(0.7):mul_dir(p), M.rotate(0.7, axes[i]):mul_dir(p), "axis constructor disagrees")
			end
			local q = Q.from_axis_angle(axes[3], math.pi / 2)
			near(q:to_mat4():mul_dir(axes[1]), axes[2], "+Z quarter turn must send +X to +Y")
			local w = lub.phys3d.world("rotation_contract", { gravity = { x = 0, y = 0, z = 0 } })
			w:begin()
			local body = lub.phys3d.body(w, "rotated", {
				type = lub.phys3d.STATIC,
				initial = { x = 2, y = 3, z = 4, quat = { x = q.x, y = q.y, z = q.z, w = q.w } },
			})
			lub.phys3d.sphere(body, "offset", { r = 0.1, offset = { x = 1, y = 0, z = 0 } })
			w:step(1 / 60)
			local pose = lub.phys3d.pose(body)
			near(
				R.pose_mat(pose):mul_point(axes[1]),
				V.new(2, 4, 4),
				"pose matrix differs from expected collider center"
			)
			assert(
				lub.phys3d.raycast(w, { x = 2, y = 4, z = 3, dx = 0, dy = 0, dz = 2 }),
				"physics collider did not rotate +X to +Y"
			)
			assert(
				not lub.phys3d.raycast(w, { x = 2, y = 2, z = 3, dx = 0, dy = 0, dz = 2 }),
				"physics collider rotated in inverse direction"
			)
		end)
		print(ok and "ROTATION_CONVENTIONS_PASS" or "ROTATION_CONVENTIONS_FAIL " .. tostring(err))
		os.exit(ok and 0 or 1, true)
	end,
}
