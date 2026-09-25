-- samples/lubx.lua (cs-lib の生成 Lua) のうち、確保しない API と FixedStep / SlotPool を検査する:
-- - lub.Math の InPlace / Set / Into 版が、同じ入力で確保版と同じ値になる。値の比較は
--   Lua の整数/浮動小数の別と 0 の符号まで含めて厳密に行う (golden がこの値に依存する)。
--   受け手や出力先が引数と同じオブジェクトの場合と、書き込み前の受け手に別の値が入っている
--   場合も確かめる。確保版が Set 版に寄せてあるものは、元の式を下の ref_* で書いて比べる。
-- - SlotPool の確保・解放・使い回し・世代・回しながらの解放・Clear。
-- - FixedStep の catch-up 上限 (既定は 50 ms 分で 60 Hz なら 3)、捨てた時間の記録、key edge が
--   1 tick で消えること。
local x = dofile("samples/lubx.lua")
local V2, V, V4, Q, M = x.Vec2, x.Vec3, x.Vec4, x.Quat, x.Mat4

local cases = 0

local function same(a, b)
	if math.type(a) ~= math.type(b) then
		return false
	end
	if a ~= a then
		return b ~= b
	end
	if a == 0 and b == 0 then
		return 1 / a == 1 / b
	end
	return a == b
end

local function show(v)
	if math.type(v) == "integer" then
		return tostring(v)
	end
	return string.format("%a", v)
end

local FIELDS = { "x", "y", "z", "w" }

local function same_obj(got, want, label)
	for _, k in ipairs(FIELDS) do
		if want[k] ~= nil then
			assert(same(got[k], want[k]), label .. ": " .. k .. " got " .. show(got[k]) .. " want " .. show(want[k]))
		end
	end
	cases = cases + 1
end

local function same_mat(got, want, label)
	local w = want.m or want
	for i = 1, 16 do
		assert(same(got.m[i], w[i]), label .. ": m[" .. (i - 1) .. "] got " .. show(got.m[i]) .. " want " .. show(w[i]))
	end
	cases = cases + 1
end

local function clone(v)
	local mt = getmetatable(v)
	if mt == V2 then
		return V2.new(v.x, v.y)
	elseif mt == V then
		return V.new(v.x, v.y, v.z)
	elseif mt == V4 then
		return V4.new(v.x, v.y, v.z, v.w)
	end
	return Q.new(v.x, v.y, v.z, v.w)
end

-- 書き込み前の受け手。結果に残っていたら書き漏れ。
local function dirty(v)
	local mt = getmetatable(v)
	if mt == V2 then
		return V2.new(7.25, -3.5)
	elseif mt == V then
		return V.new(7.25, -3.5, 11)
	elseif mt == V4 then
		return V4.new(7.25, -3.5, 11, 0.125)
	end
	return Q.new(7.25, -3.5, 11, 0.125)
end

local function dirty_mat()
	local m = M.new()
	for i = 1, 16 do
		m.m[i] = 0.5 + i
	end
	return m
end

local function clone_mat(a)
	local m = M.new()
	for i = 1, 16 do
		m.m[i] = a.m[i]
	end
	return m
end

-- Park-Miller 乱数 (Schrage の方法)。lub の Lua は整数も浮動小数も 32 bit なので、
-- 途中の値が 2^31 を超えないように計算する (超えると値が粗くなり、演算順の違いが
-- 結果に出にくい入力ばかりになる)。
local seed = 20260925
local function rnd()
	local hi, lo = seed // 127773, seed % 127773
	seed = 16807 * lo - 2836 * hi
	if seed <= 0 then
		seed = seed + 2147483647
	end
	return (seed / 2147483647) * 4 - 2
end

-- 整数成分・零・-0.0 を含む入力 (Lua では整数と浮動小数で結果の型が変わりうる)
local raw = { { 0, 1, 0, 1 }, { 0, 0, 0, 0 }, { 1, 2, 3, 4 }, { -0.0, 0.0, -1, 0.5 }, { 0.5, 0, 0, 0 } }
for _ = 1, 24 do
	raw[#raw + 1] = { rnd(), rnd(), rnd(), rnd() }
end
local TS = { 0, 0.3, 1, -0.5, 1.7 }
local SS = { 0, 2.5, -1.25, 3 }

local function vec_suite(make)
	local vs = {}
	for _, r in ipairs(raw) do
		vs[#vs + 1] = make(r)
	end
	local binary = {
		add_in_place = "add",
		sub_in_place = "sub",
		mul_in_place = "mul",
		div_in_place = "div",
		min_in_place = "min",
		max_in_place = "max",
	}
	local setters = { set_add = "add", set_sub = "sub", set_cross = "cross" }
	for i, a in ipairs(vs) do
		local b = vs[(i % #vs) + 1]
		local c = vs[((i + 3) % #vs) + 1]
		local label = "vec" .. i
		for name, alloc in pairs(binary) do
			if a[name] then
				local r = clone(a)
				assert(r[name](r, b) == r, name .. " returns self")
				same_obj(r, a[alloc](a, b), label .. " " .. name)
				r = clone(a)
				same_obj(r[name](r, r), a[alloc](a, a), label .. " " .. name .. " alias")
			end
		end
		for name, alloc in pairs(setters) do
			if a[name] then
				local want = a[alloc](a, b)
				local d = dirty(a)
				same_obj(d[name](d, a, b), want, label .. " " .. name)
				d = clone(a)
				same_obj(d[name](d, d, b), want, label .. " " .. name .. " dst=a")
				d = clone(b)
				same_obj(d[name](d, a, d), want, label .. " " .. name .. " dst=b")
				d = clone(a)
				same_obj(d[name](d, d, d), a[alloc](a, a), label .. " " .. name .. " dst=a=b")
			end
		end
		for _, s in ipairs(SS) do
			same_obj(clone(a):scale_in_place(s), a:scale(s), label .. " scale_in_place")
			same_obj(dirty(a):set_scaled(a, s), a:scale(s), label .. " set_scaled")
			same_obj(clone(a):add_scaled_in_place(b, s), a:add(b:scale(s)), label .. " add_scaled_in_place")
			local r = clone(a)
			same_obj(r:add_scaled_in_place(r, s), a:add(a:scale(s)), label .. " add_scaled_in_place alias")
		end
		for _, t in ipairs(TS) do
			same_obj(clone(a):lerp_in_place(b, t), a:lerp(b, t), label .. " lerp_in_place")
			same_obj(dirty(a):set_lerp(a, b, t), a:lerp(b, t), label .. " set_lerp")
			local d = clone(b)
			same_obj(d:set_lerp(a, d, t), a:lerp(b, t), label .. " set_lerp dst=b")
		end
		same_obj(clone(a):negate_in_place(), a:negate(), label .. " negate_in_place")
		same_obj(clone(a):normalize_in_place(), a:normalize(), label .. " normalize_in_place")
		same_obj(dirty(a):copy_from(a), a, label .. " copy_from")
		if a.clamp_in_place then
			local lo, hi = b:min(c), b:max(c)
			same_obj(clone(a):clamp_in_place(lo, hi), a:clamp(lo, hi), label .. " clamp_in_place")
		end
		if a.perp_in_place then
			same_obj(clone(a):perp_in_place(), a:perp(), label .. " perp_in_place")
		end
		if a.reflect_in_place then
			local n = b:normalize()
			local want = a:sub(n:scale(2.0 * a:dot(n)))
			same_obj(a:reflect(n), want, label .. " reflect")
			same_obj(clone(a):reflect_in_place(n), want, label .. " reflect_in_place")
			local r = clone(a)
			same_obj(r:reflect_in_place(r), a:sub(a:scale(2.0 * a:dot(a))), label .. " reflect_in_place alias")
		end
		if a.distance_sq then
			assert(same(a:distance_sq(b), a:sub(b):length_sq()), label .. " distance_sq")
		end
	end
	return vs
end

-- 確保版を Set 版に寄せる前の式
local function ref_axis_angle(axis, angle)
	local half = angle * 0.5
	local s = Math.Sin(half)
	local n = axis:normalize()
	return Q.new(n.x * s, n.y * s, n.z * s, Math.Cos(half))
end

local function ref_rotate(q, v)
	local qv = V.new(q.x, q.y, q.z)
	local uv = qv:cross(v)
	local uuv = qv:cross(uv)
	return v:add(uv:scale(2.0 * q.w):add(uuv:scale(2.0)))
end

local function ref_to_mat4(q)
	local x2, y2, z2 = q.x + q.x, q.y + q.y, q.z + q.z
	local xx, xy, xz = q.x * x2, q.x * y2, q.x * z2
	local yy, yz, zz = q.y * y2, q.y * z2, q.z * z2
	local wx, wy, wz = q.w * x2, q.w * y2, q.w * z2
	return {
		1 - (yy + zz),
		xy - wz,
		xz + wy,
		0,
		xy + wz,
		1 - (xx + zz),
		yz - wx,
		0,
		xz - wy,
		yz + wx,
		1 - (xx + yy),
		0,
		0,
		0,
		0,
		1,
	}
end

local function ref_mul(a, b)
	local p, q = a.m, b.m
	local r = {}
	for row = 0, 3 do
		for col = 0, 3 do
			local o = row * 4
			r[o + col + 1] = p[o + 1] * q[col + 1]
				+ p[o + 2] * q[4 + col + 1]
				+ p[o + 3] * q[8 + col + 1]
				+ p[o + 4] * q[12 + col + 1]
		end
	end
	return r
end

local function ref_look_at(eye, target, up)
	local z = target:sub(eye):normalize()
	local xa = up:cross(z):normalize()
	local y = z:cross(xa)
	return { xa.x, xa.y, xa.z, -xa:dot(eye), y.x, y.y, y.z, -y:dot(eye), z.x, z.y, z.z, -z:dot(eye), 0, 0, 0, 1 }
end

local function ref_quat_normalize(x, y, z, w)
	local len = Math.Sqrt(x * x + y * y + z * z + w * w)
	if len > 0 then
		return Q.new(x / len, y / len, z / len, w / len)
	end
	return Q.new(0, 0, 0, 1)
end

local function ref_slerp(a, b, t)
	local d = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w
	local bx, by, bz, bw = b.x, b.y, b.z, b.w
	if d < 0 then
		d = -d
		bx, by, bz, bw = -bx, -by, -bz, -bw
	end
	if d > 0.9995 then
		return ref_quat_normalize(
			a.x + (bx - a.x) * t,
			a.y + (by - a.y) * t,
			a.z + (bz - a.z) * t,
			a.w + (bw - a.w) * t
		)
	end
	local theta = Math.Atan2(Math.Sqrt(1.0 - d * d), d)
	local sinT = Math.Sin(theta)
	local s0 = Math.Sin((1.0 - t) * theta) / sinT
	local s1 = Math.Sin(t * theta) / sinT
	return Q.new(a.x * s0 + bx * s1, a.y * s0 + by * s1, a.z * s0 + bz * s1, a.w * s0 + bw * s1)
end

local function ref_from_euler(yaw, pitch, roll)
	local cy, sy = Math.Cos(yaw * 0.5), Math.Sin(yaw * 0.5)
	local cp, sp = Math.Cos(pitch * 0.5), Math.Sin(pitch * 0.5)
	local cr, sr = Math.Cos(roll * 0.5), Math.Sin(roll * 0.5)
	return Q.new(
		sr * cp * cy - cr * sp * sy,
		cr * sp * cy + sr * cp * sy,
		cr * cp * sy - sr * sp * cy,
		cr * cp * cy + sr * sp * sy
	)
end

local function ref_from_mat4(mat)
	local m = mat.m
	local trace = m[1] + m[6] + m[11]
	if trace > 0 then
		local s = 0.5 / Math.Sqrt(trace + 1.0)
		return Q.new((m[10] - m[7]) * s, (m[3] - m[9]) * s, (m[5] - m[2]) * s, 0.25 / s)
	elseif m[1] > m[6] and m[1] > m[11] then
		local s = 2.0 * Math.Sqrt(1.0 + m[1] - m[6] - m[11])
		return Q.new(0.25 * s, (m[2] + m[5]) / s, (m[9] + m[3]) / s, (m[10] - m[7]) / s)
	elseif m[6] > m[11] then
		local s = 2.0 * Math.Sqrt(1.0 + m[6] - m[1] - m[11])
		return Q.new((m[2] + m[5]) / s, 0.25 * s, (m[7] + m[10]) / s, (m[3] - m[9]) / s)
	end
	local s = 2.0 * Math.Sqrt(1.0 + m[11] - m[1] - m[6])
	return Q.new((m[9] + m[3]) / s, (m[7] + m[10]) / s, 0.25 * s, (m[5] - m[2]) / s)
end

local function ref_transpose(a)
	local p = a.m
	return { p[1], p[5], p[9], p[13], p[2], p[6], p[10], p[14], p[3], p[7], p[11], p[15], p[4], p[8], p[12], p[16] }
end

local function ref_rigid_inverse(a, eye)
	local p = a.m
	return { p[1], p[5], p[9], eye.x, p[2], p[6], p[10], eye.y, p[3], p[7], p[11], eye.z, 0, 0, 0, 1 }
end

local function ref_inverse(mat)
	local m = mat.m
	local a00, a01, a02, a03 = m[1], m[2], m[3], m[4]
	local a10, a11, a12, a13 = m[5], m[6], m[7], m[8]
	local a20, a21, a22, a23 = m[9], m[10], m[11], m[12]
	local a30, a31, a32, a33 = m[13], m[14], m[15], m[16]
	local b00 = a00 * a11 - a01 * a10
	local b01 = a00 * a12 - a02 * a10
	local b02 = a00 * a13 - a03 * a10
	local b03 = a01 * a12 - a02 * a11
	local b04 = a01 * a13 - a03 * a11
	local b05 = a02 * a13 - a03 * a12
	local b06 = a20 * a31 - a21 * a30
	local b07 = a20 * a32 - a22 * a30
	local b08 = a20 * a33 - a23 * a30
	local b09 = a21 * a32 - a22 * a31
	local b10 = a21 * a33 - a23 * a31
	local b11 = a22 * a33 - a23 * a32
	local det = b00 * b11 - b01 * b10 + b02 * b09 + b03 * b08 - b04 * b07 + b05 * b06
	if det == 0 then
		return { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
	end
	local inv = 1.0 / det
	return {
		(a11 * b11 - a12 * b10 + a13 * b09) * inv,
		(-a01 * b11 + a02 * b10 - a03 * b09) * inv,
		(a31 * b05 - a32 * b04 + a33 * b03) * inv,
		(-a21 * b05 + a22 * b04 - a23 * b03) * inv,
		(-a10 * b11 + a12 * b08 - a13 * b07) * inv,
		(a00 * b11 - a02 * b08 + a03 * b07) * inv,
		(-a30 * b05 + a32 * b02 - a33 * b01) * inv,
		(a20 * b05 - a22 * b02 + a23 * b01) * inv,
		(a10 * b10 - a11 * b08 + a13 * b06) * inv,
		(-a00 * b10 + a01 * b08 - a03 * b06) * inv,
		(a30 * b04 - a31 * b02 + a33 * b00) * inv,
		(-a20 * b04 + a21 * b02 - a23 * b00) * inv,
		(-a10 * b09 + a11 * b07 - a12 * b06) * inv,
		(a00 * b09 - a01 * b07 + a02 * b06) * inv,
		(-a30 * b03 + a31 * b01 - a32 * b00) * inv,
		(a20 * b03 - a21 * b01 + a22 * b00) * inv,
	}
end

local function ref_translate(x, y, z)
	return { 1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1 }
end

local function ref_scale(x, y, z)
	return { x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1 }
end

local function ref_scale_trans(s, t)
	return { s, 0, 0, t.x, 0, s, 0, t.y, 0, 0, s, t.z, 0, 0, 0, 1 }
end

local function ref_rotate_x(angle)
	local c, s = Math.Cos(angle), Math.Sin(angle)
	return { 1, 0, 0, 0, 0, c, -s, 0, 0, s, c, 0, 0, 0, 0, 1 }
end

local function ref_rotate_y(angle)
	local c, s = Math.Cos(angle), Math.Sin(angle)
	return { c, 0, s, 0, 0, 1, 0, 0, -s, 0, c, 0, 0, 0, 0, 1 }
end

local function ref_rotate_z(angle)
	local c, s = Math.Cos(angle), Math.Sin(angle)
	return { c, -s, 0, 0, s, c, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }
end

local function ref_perspective(fov, aspect, nz, fz)
	local f = 1.0 / Math.Tan(fov * math.pi / 360.0)
	return { f / aspect, 0, 0, 0, 0, f, 0, 0, 0, 0, fz / (fz - nz), -fz * nz / (fz - nz), 0, 0, 1, 0 }
end

local function ref_ortho(w, h, nz, fz)
	return { 2 / w, 0, 0, 0, 0, 2 / h, 0, 0, 0, 0, 1 / (fz - nz), -nz / (fz - nz), 0, 0, 0, 1 }
end

local function check_math()
	vec_suite(function(r)
		return V2.new(r[1], r[2])
	end)
	local vs = vec_suite(function(r)
		return V.new(r[1], r[2], r[3])
	end)
	vec_suite(function(r)
		return V4.new(r[1], r[2], r[3], r[4])
	end)
	same_obj(V2.new(1, 2):set(3, 4.5), V2.new(3, 4.5), "Vec2.set")
	same_obj(V.new(1, 2, 3):set(3, 4.5, -0.0), V.new(3, 4.5, -0.0), "Vec3.set")
	same_obj(V4.new(1, 2, 3, 4):set(3, 4.5, -0.0, 0), V4.new(3, 4.5, -0.0, 0), "Vec4.set")

	-- Quat
	local qs = { Q.new(0, 0, 0, 1), Q.new(0, 0, 0, 0), Q.new(1, 0, 0, 0), Q.new(0.5, -0.5, 0.5, 0.5) }
	for _, r in ipairs(raw) do
		qs[#qs + 1] = Q.new(r[1], r[2], r[3], r[4]):normalize()
	end
	for i, q in ipairs(qs) do
		local p = qs[(i % #qs) + 1]
		local label = "quat" .. i
		local want = q * p
		same_obj(dirty(q):set_mul(q, p), want, label .. " set_mul")
		local d = clone(q)
		same_obj(d:set_mul(d, p), want, label .. " set_mul dst=a")
		d = clone(p)
		same_obj(d:set_mul(q, d), want, label .. " set_mul dst=b")
		d = clone(q)
		same_obj(d:set_mul(d, d), q * q, label .. " set_mul dst=a=b")
		same_obj(clone(q):normalize_in_place(), q:normalize(), label .. " normalize_in_place")
		same_obj(clone(q):conjugate_in_place(), q:conjugate(), label .. " conjugate_in_place")
		same_obj(clone(q):inverse_in_place(), q:inverse(), label .. " inverse_in_place")
		same_obj(dirty(q):copy_from(q), q, label .. " copy_from")
		for _, t in ipairs(TS) do
			same_obj(clone(q):lerp_in_place(p, t), q:lerp(p, t), label .. " lerp_in_place")
			local slerp = ref_slerp(q, p, t)
			same_obj(q:slerp(p, t), slerp, label .. " slerp")
			same_obj(dirty(q):set_slerp(q, p, t), slerp, label .. " set_slerp")
			d = clone(p)
			same_obj(d:set_slerp(q, d, t), slerp, label .. " set_slerp dst=b")
			local near = ref_slerp(q, q, t)
			same_obj(q:slerp(q, t), near, label .. " slerp near")
			d = clone(q)
			same_obj(d:set_slerp(d, d, t), near, label .. " set_slerp near")
		end
		same_mat(q:to_mat4(), ref_to_mat4(q), label .. " to_mat4")
		same_mat(dirty_mat():set_from_quat(q), ref_to_mat4(q), label .. " set_from_quat")
		local qm = q:to_mat4()
		same_obj(Q.from_mat4(qm), ref_from_mat4(qm), label .. " from_mat4")
		same_obj(dirty(q):set_from_mat4(qm), ref_from_mat4(qm), label .. " set_from_mat4")
		for j, v in ipairs(vs) do
			local rv = ref_rotate(q, v)
			same_obj(q:rotate_vec3(v), rv, label .. " rotate_vec3 " .. j)
			same_obj(q:rotate_into(v, V.new(9, 9, 9)), rv, label .. " rotate_into " .. j)
			local vv = clone(v)
			assert(q:rotate_into(vv, vv) == vv, "rotate_into returns dst")
			same_obj(vv, rv, label .. " rotate_into dst=v " .. j)
		end
		local euler = ref_from_euler(q.x, q.y, q.z)
		same_obj(Q.from_euler(q.x, q.y, q.z), euler, label .. " from_euler")
		same_obj(dirty(q):set_euler(q.x, q.y, q.z), euler, label .. " set_euler")
	end
	same_obj(dirty(qs[1]):set_identity(), Q.identity(), "Quat.set_identity")
	same_obj(Q.new(1, 2, 3, 4):set(0, -0.0, 0.5, 1), Q.new(0, -0.0, 0.5, 1), "Quat.set")
	for i, axis in ipairs(vs) do
		for _, angle in ipairs({ 0, 0.7, -2.5, math.pi / 2, 1 }) do
			local want = ref_axis_angle(axis, angle)
			local label = "axis" .. i .. " " .. angle
			same_obj(Q.from_axis_angle(axis, angle), want, label .. " from_axis_angle")
			same_obj(dirty(want):set_axis_angle(axis, angle), want, label .. " set_axis_angle")
			same_mat(M.rotate(angle, axis), ref_to_mat4(want), label .. " Mat4.rotate")
			same_mat(dirty_mat():set_rotate(angle, axis), ref_to_mat4(want), label .. " set_rotate")
			same_mat(M.rotate_x(angle), ref_rotate_x(angle), label .. " rotate_x")
			same_mat(M.rotate_y(angle), ref_rotate_y(angle), label .. " rotate_y")
			same_mat(M.rotate_z(angle), ref_rotate_z(angle), label .. " rotate_z")
			same_mat(dirty_mat():set_rotate_x(angle), ref_rotate_x(angle), label .. " set_rotate_x")
			same_mat(dirty_mat():set_rotate_y(angle), ref_rotate_y(angle), label .. " set_rotate_y")
			same_mat(dirty_mat():set_rotate_z(angle), ref_rotate_z(angle), label .. " set_rotate_z")
		end
	end

	-- Mat4
	-- X / Y / Z 軸寄りの軸での半回転に近い回転は、from_mat4 の trace <= 0 側の
	-- 3 つの分岐をそれぞれ通る
	local ms = { M.new(), M.zero() }
	for _, r in ipairs({ { 0.9, 0.3, 0.2, 0.1 }, { 0.2, 0.9, 0.3, 0.1 }, { 0.3, 0.2, 0.9, 0.1 } }) do
		ms[#ms + 1] = Q.new(r[1], r[2], r[3], r[4]):normalize():to_mat4()
	end
	for i, v in ipairs(vs) do
		local w = vs[(i % #vs) + 1]
		ms[#ms + 1] = M.translate(v) * M.rotate_y(w.x) * M.scale(w)
		ms[#ms + 1] = qs[(i % #qs) + 1]:to_mat4()
	end
	-- 16 成分が全部埋まった行列。変換行列だけだと 0 の項が多く、inverse などの
	-- 式の順序が変わっても結果が変わらないことがある
	for _ = 1, 8 do
		local g = M.new()
		for k = 1, 16 do
			g.m[k] = rnd()
		end
		ms[#ms + 1] = g
	end
	same_mat(dirty_mat():set_identity(), M.identity(), "set_identity")
	same_mat(dirty_mat():set_zero(), M.zero(), "set_zero")
	same_mat(M.zero(), { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, "Mat4.zero")
	for i, a in ipairs(ms) do
		local b = ms[(i % #ms) + 1]
		local v = vs[(i % #vs) + 1]
		local w = vs[((i + 5) % #vs) + 1]
		local label = "mat" .. i
		local want = ref_mul(a, b)
		same_mat(a * b, want, label .. " mul")
		same_mat(dirty_mat():set_mul(a, b), want, label .. " set_mul")
		local d = clone_mat(a)
		same_mat(d:set_mul(d, b), want, label .. " set_mul dst=a")
		d = clone_mat(b)
		same_mat(d:set_mul(a, d), want, label .. " set_mul dst=b")
		d = clone_mat(a)
		same_mat(d:set_mul(d, d), ref_mul(a, a), label .. " set_mul dst=a=b")
		same_mat(dirty_mat():copy_from(a), a, label .. " copy_from")
		local tr = ref_transpose(a)
		same_mat(a:transpose(), tr, label .. " transpose")
		same_mat(dirty_mat():set_transpose(a), tr, label .. " set_transpose")
		d = clone_mat(a)
		same_mat(d:set_transpose(d), tr, label .. " set_transpose alias")
		local inv = ref_inverse(a)
		same_mat(a:inverse(), inv, label .. " inverse")
		same_mat(dirty_mat():set_inverse(a), inv, label .. " set_inverse")
		d = clone_mat(a)
		same_mat(d:set_inverse(d), inv, label .. " set_inverse alias")
		local rinv = ref_rigid_inverse(a, v)
		same_mat(a:rigid_inverse(v), rinv, label .. " rigid_inverse")
		same_mat(dirty_mat():set_rigid_inverse(a, v), rinv, label .. " set_rigid_inverse")
		d = clone_mat(a)
		same_mat(d:set_rigid_inverse(d, v), rinv, label .. " set_rigid_inverse alias")
		same_obj(Q.from_mat4(a), ref_from_mat4(a), label .. " from_mat4")
		same_obj(a:mul_point_into(v, V.new(9, 9, 9)), a:mul_point(v), label .. " mul_point_into")
		same_obj(a:mul_dir_into(v, V.new(9, 9, 9)), a:mul_dir(v), label .. " mul_dir_into")
		local vv = clone(v)
		same_obj(a:mul_point_into(vv, vv), a:mul_point(v), label .. " mul_point_into dst=v")
		vv = clone(v)
		same_obj(a:mul_dir_into(vv, vv), a:mul_dir(v), label .. " mul_dir_into dst=v")
		local v4 = V4.new(v.x, v.y, v.z, w.x)
		same_obj(a:mul_vec4_into(v4, V4.new(9, 9, 9, 9)), a * v4, label .. " mul_vec4_into")
		local vv4 = clone(v4)
		same_obj(a:mul_vec4_into(vv4, vv4), a * v4, label .. " mul_vec4_into dst=v")
		local t = ref_translate(v.x, v.y, v.z)
		same_mat(M.translate(v), t, label .. " translate")
		same_mat(M.translate_xyz(v.x, v.y, v.z), t, label .. " translate_xyz")
		same_mat(dirty_mat():set_translate(v.x, v.y, v.z), t, label .. " set_translate")
		local sc = ref_scale(v.x, v.y, v.z)
		same_mat(M.scale(v), sc, label .. " scale")
		same_mat(M.scale_xyz(v.x, v.y, v.z), sc, label .. " scale_xyz")
		same_mat(dirty_mat():set_scale(v.x, v.y, v.z), sc, label .. " set_scale")
		local st = ref_scale_trans(w.y, v)
		same_mat(M.scale_trans(w.y, v), st, label .. " scale_trans")
		same_mat(dirty_mat():set_scale_trans(w.y, v.x, v.y, v.z), st, label .. " set_scale_trans")
		local up = vs[((i + 2) % #vs) + 1]
		local look = ref_look_at(v, w, up)
		same_mat(M.look_at_lh(v, w, up), look, label .. " look_at_lh")
		same_mat(dirty_mat():set_look_at_lh(v, w, up), look, label .. " set_look_at_lh")
		same_mat(dirty_mat():set_look_at_lh(v, v, up), ref_look_at(v, v, up), label .. " set_look_at_lh eye=target")
		local persp = ref_perspective(40 + i, 1.5 + w.x, 0.1, 100 + i)
		same_mat(M.perspective_lh(40 + i, 1.5 + w.x, 0.1, 100 + i), persp, label .. " perspective_lh")
		same_mat(dirty_mat():set_perspective_lh(40 + i, 1.5 + w.x, 0.1, 100 + i), persp, label .. " set_perspective_lh")
		local ortho = ref_ortho(10 + v.x, 8 + v.y, -1 + v.z, 50)
		same_mat(M.ortho_lh(10 + v.x, 8 + v.y, -1 + v.z, 50), ortho, label .. " ortho_lh")
		same_mat(dirty_mat():set_ortho_lh(10 + v.x, 8 + v.y, -1 + v.z, 50), ortho, label .. " set_ortho_lh")
		-- SetTrs は T * R * S と値で一致する (0 の符号と整数/浮動小数の別は問わない)
		local q = qs[(i % #qs) + 1]
		local trs = M.translate(v) * q:to_mat4() * M.scale(w)
		local got = dirty_mat():set_trs(v.x, v.y, v.z, q.x, q.y, q.z, q.w, w.x, w.y, w.z)
		for k = 1, 16 do
			assert(got.m[k] == trs.m[k], label .. " set_trs m[" .. (k - 1) .. "]")
		end
		cases = cases + 1
	end
end

local function check_slot_pool()
	local P = x.SlotPool
	local p = P.new()
	assert(p.capacity == 0 and p.live == 0, "empty pool")
	assert(p:alloc() == 0 and p:alloc() == 1 and p:alloc() == 2, "alloc appends")
	assert(p.capacity == 3 and p.live == 3, "capacity/live after alloc")
	assert(p:generation(1) == 1, "first generation is 1")
	p:free(1)
	assert(not p:is_alive(1) and p.live == 2, "free")
	p:free(1)
	p:free(-1)
	p:free(99)
	assert(p.live == 2 and p.capacity == 3, "free of dead/out-of-range is a no-op")
	assert(not p:is_alive(-1) and not p:is_alive(3), "is_alive out of range")
	assert(p:generation(-1) == 0 and p:generation(3) == 0, "generation out of range")
	assert(p:alloc() == 1 and p:is_alive(1) and p:generation(1) == 2, "reuse bumps generation")
	p:free(0)
	p:free(2)
	assert(p:alloc() == 2 and p:alloc() == 0, "reuse is LIFO")
	assert(p:alloc() == 3 and p.capacity == 4 and p.live == 4, "append when no free slot")
	cases = cases + 1

	-- 回しながら解放する: 全部 1 回ずつ訪れ、詰め直しで飛ばされない
	local q = P.new()
	local items = {}
	for i = 1, 10 do
		local s = q:alloc()
		items[s + 1] = { id = s }
	end
	local visited = 0
	local i = 0
	while i < q.capacity do
		if q:is_alive(i) then
			visited = visited + 1
			assert(items[i + 1].id == i, "item stays at its slot")
			if i % 2 == 0 then
				q:free(i)
			end
		end
		i = i + 1
	end
	assert(visited == 10 and q.live == 5, "free during iteration")
	for s = 0, 9 do
		assert(q:is_alive(s) == (s % 2 == 1), "alive after iteration " .. s)
	end
	q:clear()
	assert(q.live == 0 and q.capacity == 10, "clear keeps capacity")
	for s = 0, 9 do
		assert(not q:is_alive(s), "clear frees " .. s)
	end
	for s = 0, 9 do
		assert(q:alloc() == s, "alloc after clear goes in order")
		assert(q:generation(s) == 2, "generation after clear " .. s)
	end
	assert(q:alloc() == 10 and q.capacity == 11, "append after clear")
	P.new():clear()
	cases = cases + 1
end

local function check_fixed_step()
	local FS = x.FixedStep
	local input = lub.input
	local saved = { input.key_pressed, input.key_released, input.mouse_pressed, input.mouse_released }
	local pressed, released, mouse = {}, {}, {}
	input.key_pressed = function(k)
		return pressed[k] == true
	end
	input.key_released = function(k)
		return released[k] == true
	end
	input.mouse_pressed = function(b)
		return mouse[b] == true
	end
	input.mouse_released = function()
		return false
	end
	local ok, err = pcall(function()
		local T = 1.0 / 60.0

		-- 0.5 秒の引っかかり: 既定の上限 3 tick だけ回し、残りは全部捨てる
		local s = FS.new()
		local n = 0
		s:frame(0.5, function(dt)
			assert(dt == T, "tick dt")
			n = n + 1
		end)
		local acc = 0.5
		for _ = 1, 3 do
			acc = acc - T
		end
		assert(n == 3 and s.last_steps == 3, "hitch runs maxCatchUp=3 ticks, got " .. n)
		assert(s.last_dropped == acc, "last_dropped " .. show(s.last_dropped) .. " want " .. show(acc))
		assert(s.total_dropped == acc, "total_dropped")
		assert(s:alpha() == 0, "nothing carried over after a drop")
		n = 0
		s:frame(T, function()
			n = n + 1
		end)
		assert(n == 1 and s.last_steps == 1 and s.last_dropped == 0, "next frame runs one tick")
		assert(s.total_dropped == acc, "total_dropped accumulates only drops")
		s:reset_dropped()
		assert(s.total_dropped == 0, "reset_dropped")
		cases = cases + 1

		-- 上限は引数で変えられる
		local s8 = FS.new(nil, 8)
		n = 0
		s8:frame(0.5, function()
			n = n + 1
		end)
		assert(n == 8 and s8.last_steps == 8, "maxCatchUp=8")
		cases = cases + 1

		-- 既定の上限は 50 ms 分 (hz / 20 の切り上げ、最低 3)
		for _, c in ipairs({ { 30, 3 }, { 60, 3 }, { 61, 4 }, { 120, 6 }, { 240, 12 } }) do
			local hs = FS.new(c[1])
			n = 0
			hs:frame(0.5, function()
				n = n + 1
			end)
			assert(n == c[2] and hs.last_steps == c[2], "default maxCatchUp at " .. c[1] .. " Hz, got " .. n)
		end
		-- hz を上げても、表示が 20 fps 以上なら毎フレーム実時間どおりに進む
		for _, c in ipairs({ { 240, 60 }, { 240, 30 }, { 120, 30 }, { 120, 20 } }) do
			local hz, fps = c[1], c[2]
			local hs = FS.new(hz)
			n = 0
			for _ = 1, fps do
				hs:frame(1 / fps, function()
					n = n + 1
				end)
			end
			assert(n == hz and hs.total_dropped == 0, hz .. " Hz at " .. fps .. " fps ran " .. n .. " ticks in 1 s")
		end
		cases = cases + 1

		-- 上限に届かない dt 列では、打ち切りの位置を変える前 (loop 前に
		-- min(acc + dt, T * max) で丸めていた) と同じ tick 数になる
		local function old_ticks(dts, cap)
			local a, out = 0, {}
			for _, dt in ipairs(dts) do
				a = Math.Min(a + dt, T * cap)
				local k = 0
				while a + 1e-9 >= T and k < cap do
					a = a - T
					if a < 0 then
						a = 0
					end
					k = k + 1
				end
				out[#out + 1] = k
			end
			return out
		end
		local dts = {}
		for f = 1, 300 do
			dts[f] = 0.0166666666666667
		end
		for f = 1, 200 do
			dts[#dts + 1] = ({ 1 / 60, 1 / 30, 1 / 144, 0.02, 0.045, 0.001 })[(f % 6) + 1]
		end
		local want = old_ticks(dts, 8)
		local s3 = FS.new()
		for f, dt in ipairs(dts) do
			s3:frame(dt, function() end)
			assert(s3.last_steps == want[f], "tick count differs at frame " .. f)
			assert(s3.last_dropped == 0, "nothing dropped at frame " .. f)
			if f <= 300 then
				assert(s3.last_steps == 1, "one tick per frame at fixed dt")
			end
		end
		cases = cases + 1

		-- Stop(): tick の中なら今の tick の分を引いた残りを捨てる
		local st = FS.new()
		n = 0
		st:frame(0.1, function()
			n = n + 1
			if n == 2 then
				st:stop()
			end
		end)
		assert(n == 2 and st.last_steps == 2, "stop ends the frame")
		assert(st.last_dropped == (0.1 - T) - T, "stop records the rest as dropped")
		st:frame(0.01, function()
			error("no tick expected")
		end)
		st:stop()
		assert(st.total_dropped == ((0.1 - T) - T) + 0.01, "stop outside a tick drops the carried time")
		assert(st:alpha() == 0, "stop clears carried time")
		cases = cases + 1

		-- key edge は 1 tick で消え、tick 0 回のフレームの edge は次の tick へ持ち越す
		local e = FS.new()
		local seen = {}
		local function tick()
			seen[#seen + 1] = {
				r = e:key_pressed("r"),
				space = e:key_pressed("space"),
				nine = e:key_pressed("9"),
				r_up = e:key_released("r"),
				m1 = e:mouse_pressed(1),
				m2 = e:mouse_pressed(2),
			}
		end
		pressed = { r = true, space = true, ["9"] = true }
		mouse = { [1] = true }
		e:frame(T, tick)
		pressed, mouse = {}, {}
		e:frame(T, tick)
		e:frame(T, tick)
		assert(#seen == 3, "three ticks")
		assert(seen[1].r and seen[1].space and seen[1].nine and seen[1].m1, "edges seen on the first tick")
		assert(not seen[1].m2 and not seen[1].r_up, "no other edges")
		for k = 2, 3 do
			assert(
				not seen[k].r and not seen[k].space and not seen[k].nine and not seen[k].m1,
				"edges cleared after one tick (tick " .. k .. ")"
			)
		end
		seen = {}
		pressed, released = { r = true }, {}
		e:frame(0.001, tick)
		assert(#seen == 0, "no tick on a short frame")
		pressed, released = {}, { r = true }
		e:frame(T, tick)
		released = {}
		e:frame(T, tick)
		assert(#seen == 2 and seen[1].r and seen[1].r_up, "edge from a tick-less frame reaches the next tick")
		assert(not seen[2].r and not seen[2].r_up, "carried edge cleared after one tick")
		cases = cases + 1
	end)
	input.key_pressed, input.key_released, input.mouse_pressed, input.mouse_released = table.unpack(saved)
	if not ok then
		error(err, 0)
	end
end

return {
	on_init = function()
		lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	end,
	on_frame = function()
		local ok, err = pcall(function()
			check_math()
			check_slot_pool()
			check_fixed_step()
		end)
		print(ok and ("LUBX_MATH_INPLACE_PASS cases=" .. cases) or ("LUBX_MATH_INPLACE_FAIL " .. tostring(err)))
		os.exit(ok and 0 or 1, true)
	end,
}
