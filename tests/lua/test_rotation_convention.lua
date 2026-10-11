-- 回転の規約がスタック全体で 1 つであることを検査する:
-- Quat.RotateVec3、Quat.ToMat4、Mat4.Rotate、Mat4.RotateX/Y/Z、そして
-- Phys3d (box3d) が pose の quaternion で回すローカル点は全部同じ写像。
-- 期待値は Rodrigues の式で独立に計算する。
local x = dofile("samples/lubx.lua")
local V, Q, M, R = x.Vec3, x.Quat, x.Mat4, x.Renderer3d

local function near(a, b, label)
	local ok = math.abs(a.X - b.X) < 1e-5 and math.abs(a.Y - b.Y) < 1e-5 and math.abs(a.Z - b.Z) < 1e-5
	assert(
		ok,
		label
			.. ": got ("
			.. a.X
			.. ", "
			.. a.Y
			.. ", "
			.. a.Z
			.. ") expected ("
			.. b.X
			.. ", "
			.. b.Y
			.. ", "
			.. b.Z
			.. ")"
	)
end

local function near_quat(a, b, label)
	-- q と -q は同じ回転
	local sign = (a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W) < 0 and -1 or 1
	local ok = math.abs(a.X - sign * b.X) < 1e-5
		and math.abs(a.Y - sign * b.Y) < 1e-5
		and math.abs(a.Z - sign * b.Z) < 1e-5
		and math.abs(a.W - sign * b.W) < 1e-5
	assert(ok, label)
end

-- 右ねじの能動回転 (Rodrigues)。axis は正規化済み。
local function rodrigues(axis, angle, p)
	local c, s = math.cos(angle), math.sin(angle)
	local dot = axis.X * p.X + axis.Y * p.Y + axis.Z * p.Z
	return V.new(
		p.X * c + (axis.Y * p.Z - axis.Z * p.Y) * s + axis.X * dot * (1 - c),
		p.Y * c + (axis.Z * p.X - axis.X * p.Z) * s + axis.Y * dot * (1 - c),
		p.Z * c + (axis.X * p.Y - axis.Y * p.X) * s + axis.Z * dot * (1 - c)
	)
end

local cases = 0

local function check_math()
	local X, Y, Z = V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1)
	-- 規約の定義そのもの: +Z 回りの +90 度は +X を +Y に、+Y 回りの +90 度は +Z を +X に回す
	near(Q.FromAxisAngle(Z, math.pi / 2):RotateVec3(X), Y, "Z +90deg maps +X to +Y")
	near(M.RotateZ(math.pi / 2):MulDir(X), Y, "Mat4.RotateZ +90deg maps +X to +Y")
	near(M.RotateY(math.pi / 2):MulDir(Z), X, "Mat4.RotateY +90deg maps +Z to +X")
	near(M.RotateX(math.pi / 2):MulDir(Y), Z, "Mat4.RotateX +90deg maps +Y to +Z")

	local p = V.new(0.7, -0.4, 0.3)
	local axes = { X, Y, Z, V.new(1, 2, 3):Normalize() }
	local principal = { M.RotateX, M.RotateY, M.RotateZ }
	for i, axis in ipairs(axes) do
		for _, angle in ipairs({ math.pi / 2, -0.7, 0, 2.5 }) do
			local expected = rodrigues(axis, angle, p)
			local q = Q.FromAxisAngle(axis, angle)
			near(q:RotateVec3(p), expected, "Quat.RotateVec3")
			near(q:ToMat4():MulDir(p), expected, "Quat.ToMat4")
			near(M.Rotate(angle, axis):MulDir(p), expected, "Mat4.Rotate")
			near(M.FromQuat(q):MulDir(p), expected, "Mat4.FromQuat")
			if principal[i] then
				near(principal[i](angle):MulDir(p), expected, "Mat4.RotateX/Y/Z")
			end
			near_quat(Q.FromMat4(q:ToMat4()), q, "Quat.FromMat4 inverts ToMat4")
			cases = cases + 1
		end
	end

	-- 合成: a:Mul(b) は b を先に適用。行列の積と一致する。演算子 `*` は C# の
	-- 呼び出し箇所で静的に解決され、Lua の metamethod にはならないので Mul で呼ぶ
	local a = Q.FromAxisAngle(axes[4], 1.1)
	local b = Q.FromAxisAngle(Y, -0.6)
	near(a:Mul(b):RotateVec3(p), a:RotateVec3(b:RotateVec3(p)), "Quat.Mul applies b first")
	near(a:Mul(b):ToMat4():MulDir(p), a:ToMat4():Mul(b:ToMat4()):MulDir(p), "Quat.Mul matches Mat4.Mul")
	-- FromEuler: roll (X) → pitch (Y) → yaw (Z) の順に適用
	local e = Q.FromEuler(0.4, -0.3, 0.9)
	local composed = Q.FromAxisAngle(Z, 0.4):Mul(Q.FromAxisAngle(Y, -0.3)):Mul(Q.FromAxisAngle(X, 0.9))
	near_quat(e, composed, "Quat.FromEuler order")
end

-- 物理 (box3d) が pose の quaternion で回した collider の位置と、PoseMat で
-- 描画側が置く頂点位置が一致する。raycast で実 collider を当てて確かめる。
local function check_physics()
	local p = V.new(0.7, -0.4, 0.3)
	local axes = { V.new(1, 0, 0), V.new(0, 1, 0), V.new(0, 0, 1), V.new(1, 2, 3):Normalize() }
	for i, axis in ipairs(axes) do
		for j, angle in ipairs({ math.pi / 2, -0.7, 0 }) do
			local expected = rodrigues(axis, angle, p) + V.new(2, 3, 4)
			local q = Q.FromAxisAngle(axis, angle)
			local w = lub.phys3d.world("rotation_convention:" .. i .. ":" .. j, { gravity = { x = 0, y = 0, z = 0 } })
			w:begin()
			local body = lub.phys3d.body(w, "body", {
				type = lub.phys3d.STATIC,
				initial = { x = 2, y = 3, z = 4, quat = { x = q.X, y = q.Y, z = q.Z, w = q.W } },
			})
			lub.phys3d.sphere(body, "offset", { r = 0.05, offset = { x = p.X, y = p.Y, z = p.Z } })
			w:step(1 / 60)
			local pose = lub.phys3d.pose(body)
			near(R.PoseMat(pose):MulPoint(p), expected, "PoseMat differs from physics rotation")
			local hit =
				lub.phys3d.raycast(w, { x = expected.X, y = expected.Y, z = expected.Z - 0.5, dx = 0, dy = 0, dz = 1 })
			assert(hit, "expected point is not at the real collider")
			cases = cases + 1
		end
	end
end

return {
	OnInit = function()
		lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	end,
	OnFrame = function()
		local ok, err = pcall(function()
			check_math()
			check_physics()
		end)
		print(ok and ("ROTATION_CONVENTION_PASS cases=" .. cases) or ("ROTATION_CONVENTION_FAIL " .. tostring(err)))
		os.exit(ok and 0 or 1, true)
	end,
}
