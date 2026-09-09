-- 回転の規約がスタック全体で 1 つであることを検査する:
-- Quat.RotateVec3、Quat.ToMat4、Mat4.Rotate、Mat4.RotateX/Y/Z、そして
-- Phys3d (box3d) が pose の quaternion で回すローカル点は全部同じ写像。
-- 期待値は Rodrigues の式で独立に計算する。
local x = dofile("samples/lubx.lua")
local V, Q, M, R = x.Vec3, x.Quat, x.Mat4, x.Renderer3d

local function near(a, b, label)
	local ok = math.abs(a.x - b.x) < 1e-5 and math.abs(a.y - b.y) < 1e-5 and math.abs(a.z - b.z) < 1e-5
	assert(
		ok,
		label
			.. ": got ("
			.. a.x
			.. ", "
			.. a.y
			.. ", "
			.. a.z
			.. ") expected ("
			.. b.x
			.. ", "
			.. b.y
			.. ", "
			.. b.z
			.. ")"
	)
end

local function near_quat(a, b, label)
	-- q と -q は同じ回転
	local sign = (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w) < 0 and -1 or 1
	local ok = math.abs(a.x - sign * b.x) < 1e-5
		and math.abs(a.y - sign * b.y) < 1e-5
		and math.abs(a.z - sign * b.z) < 1e-5
		and math.abs(a.w - sign * b.w) < 1e-5
	assert(ok, label)
end

-- 右ねじの能動回転 (Rodrigues)。axis は正規化済み。
local function rodrigues(axis, angle, p)
	local c, s = math.cos(angle), math.sin(angle)
	local dot = axis.x * p.x + axis.y * p.y + axis.z * p.z
	return V.new(
		p.x * c + (axis.y * p.z - axis.z * p.y) * s + axis.x * dot * (1 - c),
		p.y * c + (axis.z * p.x - axis.x * p.z) * s + axis.y * dot * (1 - c),
		p.z * c + (axis.x * p.y - axis.y * p.x) * s + axis.z * dot * (1 - c)
	)
end

local cases = 0

local function check_math()
	local X, Y, Z = V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1)
	-- 規約の定義そのもの: +Z 回りの +90 度は +X を +Y に、+Y 回りの +90 度は +Z を +X に回す
	near(Q.from_axis_angle(Z, math.pi / 2):rotate_vec3(X), Y, "Z +90deg maps +X to +Y")
	near(M.rotate_z(math.pi / 2):mul_dir(X), Y, "Mat4.RotateZ +90deg maps +X to +Y")
	near(M.rotate_y(math.pi / 2):mul_dir(Z), X, "Mat4.RotateY +90deg maps +Z to +X")
	near(M.rotate_x(math.pi / 2):mul_dir(Y), Z, "Mat4.RotateX +90deg maps +Y to +Z")

	local p = V.new(0.7, -0.4, 0.3)
	local axes = { X, Y, Z, V.new(1, 2, 3):normalize() }
	local principal = { M.rotate_x, M.rotate_y, M.rotate_z }
	for i, axis in ipairs(axes) do
		for _, angle in ipairs({ math.pi / 2, -0.7, 0, 2.5 }) do
			local expected = rodrigues(axis, angle, p)
			local q = Q.from_axis_angle(axis, angle)
			near(q:rotate_vec3(p), expected, "Quat.RotateVec3")
			near(q:to_mat4():mul_dir(p), expected, "Quat.ToMat4")
			near(M.rotate(angle, axis):mul_dir(p), expected, "Mat4.Rotate")
			near(M.from_quat(q):mul_dir(p), expected, "Mat4.FromQuat")
			if principal[i] then
				near(principal[i](angle):mul_dir(p), expected, "Mat4.RotateX/Y/Z")
			end
			near_quat(Q.from_mat4(q:to_mat4()), q, "Quat.FromMat4 inverts ToMat4")
			cases = cases + 1
		end
	end

	-- 合成: a * b は b を先に適用。行列の積と一致する
	local a = Q.from_axis_angle(axes[4], 1.1)
	local b = Q.from_axis_angle(Y, -0.6)
	near((a * b):rotate_vec3(p), a:rotate_vec3(b:rotate_vec3(p)), "Quat.Mul applies b first")
	near((a * b):to_mat4():mul_dir(p), (a:to_mat4() * b:to_mat4()):mul_dir(p), "Quat.Mul matches Mat4.Mul")
	-- FromEuler: roll (X) → pitch (Y) → yaw (Z) の順に適用
	local e = Q.from_euler(0.4, -0.3, 0.9)
	local composed = Q.from_axis_angle(Z, 0.4) * Q.from_axis_angle(Y, -0.3) * Q.from_axis_angle(X, 0.9)
	near_quat(e, composed, "Quat.FromEuler order")
end

-- 物理 (box3d) が pose の quaternion で回した collider の位置と、PoseMat で
-- 描画側が置く頂点位置が一致する。raycast で実 collider を当てて確かめる。
local function check_physics()
	local p = V.new(0.7, -0.4, 0.3)
	local axes = { V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1), V.new(1, 2, 3):normalize() }
	for i, axis in ipairs(axes) do
		for j, angle in ipairs({ math.pi / 2, -0.7, 0 }) do
			local expected = rodrigues(axis, angle, p) + V.new(2, 3, 4)
			local q = Q.from_axis_angle(axis, angle)
			local w = lub.phys3d.world("rotation_convention:" .. i .. ":" .. j, { gravity = { x = 0, y = 0, z = 0 } })
			w:begin()
			local body = lub.phys3d.body(w, "body", {
				type = lub.phys3d.STATIC,
				initial = { x = 2, y = 3, z = 4, quat = { x = q.x, y = q.y, z = q.z, w = q.w } },
			})
			lub.phys3d.sphere(body, "offset", { r = 0.05, offset = { x = p.x, y = p.y, z = p.z } })
			w:step(1 / 60)
			local pose = lub.phys3d.pose(body)
			near(R.pose_mat(pose):mul_point(p), expected, "PoseMat differs from physics rotation")
			local hit =
				lub.phys3d.raycast(w, { x = expected.x, y = expected.y, z = expected.z - 0.5, dx = 0, dy = 0, dz = 1 })
			assert(hit, "expected point is not at the real collider")
			cases = cases + 1
		end
	end
end

return {
	on_init = function()
		lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	end,
	on_frame = function()
		local ok, err = pcall(function()
			check_math()
			check_physics()
		end)
		print(ok and ("ROTATION_CONVENTION_PASS cases=" .. cases) or ("ROTATION_CONVENTION_FAIL " .. tostring(err)))
		os.exit(ok and 0 or 1, true)
	end,
}
