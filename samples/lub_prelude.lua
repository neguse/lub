-- lub の公式 script API 面。
-- `lub` table (lub.gfx.begin_pass / lub.phys2d.world / ...) は C runtime が
-- cs-lib/lub_stub.cs からの生成物 (src/gen/lua_api_gen.c) で注入する。これが
-- C# (tcs) と raw Lua の面。
-- ここは Haxe extern の emit 形のための互換層で、Haxe 撤去 (段階 8) で消える:
--   - PascalCase の namespace table (Gfx.begin_pass) と lub.Gfx (実体は同一)
--   - flat global (begin_pass, phys2d_world, ui_begin, ...)
--   - Haxe 時代の 1 関数 2 形 (phys2d_pose(world, key) 等) と別名 field の吸収

local gfx, input, io, png = lub.gfx, lub.input, lub.io, lub.png
local phys2d, phys3d = lub.phys2d, lub.phys3d

-- ---------------------------------------------------------- namespaces
-- Haxe 向けの table は生成物の table を __index に持つ写しで、旧 flat 名
-- (phys2d_world / ui_begin / audio_snd 等) の alias と、2 形の吸収を重ねる。
-- 生成物 (lub.gfx 等) はそのまま。

-- Haxe の typedef は camelCase (fixedDt / maxSteps / bodyA) で、旧 binding は
-- snake_case と両方読んだ。生成した binding は snake_case だけを読むので、
-- table の引数を再帰的に snake_case に写す (sentinel と view は触らない)。
local function snake_key(k)
	return (k:gsub("(%u)", function(c)
		return "_" .. c:lower()
	end))
end

-- points = { {x, y}, ... } / { {x = .., y = ..}, ... } を平らな数値列にする
local function flatten_points(pts)
	if type(pts) ~= "table" or type(pts[1]) ~= "table" then
		return pts
	end
	local flat = {}
	for _, e in ipairs(pts) do
		if e.x ~= nil then
			flat[#flat + 1] = e.x
			flat[#flat + 1] = e.y
			if e.z ~= nil then
				flat[#flat + 1] = e.z
			end
		else
			for _, n in ipairs(e) do
				flat[#flat + 1] = n
			end
		end
	end
	return flat
end

-- 64 bit の bit mask を 16 桁 hex にする (LUA_32BITS でも壊れないよう 32 bit
-- ずつ組む)
local function bits_hex(hi, lo)
	return string.format("%08x%08x", hi, lo)
end

local function index_bit(i)
	if i >= 32 then
		return 1 << (i - 32), 0
	end
	return 0, 1 << i
end

-- filter の旧形 (category = bit 番号、mask = bit 番号の列か "all") を
-- category_bits / mask_bits (hex 文字列) に写す
local function normalize_filter(f)
	if type(f) ~= "table" then
		return
	end
	if f.category ~= nil and f.category_bits == nil then
		local hi, lo = index_bit(f.category)
		f.category_bits = bits_hex(hi, lo)
	end
	if f.mask ~= nil and f.mask_bits == nil then
		if f.mask == "all" then
			f.mask_bits = "ffffffffffffffff"
		elseif type(f.mask) == "table" then
			local hi, lo = 0, 0
			for _, i in ipairs(f.mask) do
				local h, l = index_bit(i)
				hi = hi | h
				lo = lo | l
			end
			f.mask_bits = bits_hex(hi, lo)
		end
	end
end

local function normalize_keys(t, depth)
	if type(t) ~= "table" or t.__lub_kind ~= nil or depth > 8 then
		return t
	end
	if type(t.points) == "table" then
		t.points = flatten_points(t.points)
	end
	normalize_filter(t.filter)
	if t.category ~= nil or t.mask ~= nil then
		normalize_filter(t)
	end
	local added = nil
	for k, v in pairs(t) do
		if type(k) == "string" then
			if k:find("%u") then
				local sk = snake_key(k)
				if t[sk] == nil then
					added = added or {}
					added[sk] = v
				end
			end
			if type(v) == "table" then
				normalize_keys(v, depth + 1)
			end
		elseif type(v) == "table" then
			normalize_keys(v, depth + 1)
		end
	end
	if added then
		for k, v in pairs(added) do
			t[k] = v
		end
	end
	return t
end

local function normalized(fn)
	return function(...)
		local args = { ... }
		for i = 1, select("#", ...) do
			if type(args[i]) == "table" then
				normalize_keys(args[i], 0)
			end
		end
		return fn(table.unpack(args, 1, select("#", ...)))
	end
end

-- extra は互換の上書き。normalize が真なら table 引数の key を snake_case に写す。
local function compat(ns, prefix, extra, normalize)
	local t = setmetatable({}, { __index = ns })
	for name, fn in pairs(ns) do
		if type(fn) == "function" then
			local f = normalize and normalized(fn) or fn
			if normalize then
				t[name] = f
			end
			if prefix then
				t[prefix .. name] = f
			end
		end
	end
	for name, fn in pairs(extra or {}) do
		local f = normalize and normalized(fn) or fn
		t[name] = f
		if prefix then
			t[prefix .. name] = f
		end
	end
	return t
end

-- use_buffer(key, type, count) は空確保
local function use_buffer(key, kind, data, version)
	if type(data) == "number" then
		return gfx.use_buffer_empty(key, kind, data, version)
	end
	return gfx.use_buffer(key, kind, data, version)
end

-- audio_snd の data が Bytes / string なら f32 の byte 列
local function audio_snd(key, data, channels, rate, version)
	if type(data) ~= "table" then
		return lub.audio.snd_bytes(key, data, channels, rate, version)
	end
	return lub.audio.snd(key, data, channels, rate, version)
end

-- pose(world, key) / raycast(world, q, visitor) / shape_cast(world, q, visitor)
local function dual_forms(P)
	return {
		pose = function(a, key)
			if key ~= nil then
				return P.pose_by_key(a, key)
			end
			return P.pose(a)
		end,
		raycast = function(world, q, visitor)
			if visitor ~= nil then
				return P.raycast_all(world, q, visitor)
			end
			return P.raycast(world, q)
		end,
		shape_cast = function(world, q, visitor)
			-- proxy の type / shape は kind、r は radius の旧名
			if type(q) == "table" then
				if q.kind == nil then
					q.kind = q.type or q.shape
				end
				if q.radius == nil and q.r ~= nil then
					q.radius = q.r
				end
			end
			if visitor ~= nil then
				return P.shape_cast_all(world, q, visitor)
			end
			return P.shape_cast(world, q)
		end,
		-- set_velocity の x / y / z は vx / vy / vz の旧名
		set_velocity = function(body, desc, opts)
			if type(desc) == "table" then
				if desc.vx == nil and desc.x ~= nil then
					desc.vx = desc.x
				end
				if desc.vy == nil and desc.y ~= nil then
					desc.vy = desc.y
				end
				if desc.vz == nil and desc.z ~= nil then
					desc.vz = desc.z
				end
			end
			return P.set_velocity(body, desc, opts)
		end,
		-- set_mass_data の center / cx, cy / rotational_inertia は旧名
		set_mass_data = function(body, desc, opts)
			if type(desc) == "table" then
				if desc.local_center == nil then
					if desc.center ~= nil then
						desc.local_center = desc.center
					elseif desc.cx ~= nil or desc.cy ~= nil then
						desc.local_center = { x = desc.cx or 0, y = desc.cy or 0 }
					end
				end
				if desc.inertia == nil and desc.rotational_inertia ~= nil then
					desc.inertia = desc.rotational_inertia
				end
			end
			return P.set_mass_data(body, desc, opts)
		end,
		-- joint の a / b は body_a / body_b の旧名
		joint = function(world, key, desc)
			if type(desc) == "table" then
				if desc.body_a == nil then
					desc.body_a = desc.a
				end
				if desc.body_b == nil then
					desc.body_b = desc.b
				end
			end
			return P.joint(world, key, desc)
		end,
	}
end

-- set_target の dt は time_step の旧名
local function set_target_2d(body, target, opts)
	if opts and opts.dt ~= nil and opts.time_step == nil then
		opts.time_step = opts.dt
	end
	return phys2d.set_target(body, target, opts)
end
local function set_target_3d(body, desc)
	if desc and desc.dt ~= nil and desc.time_step == nil then
		desc.time_step = desc.dt
	end
	return phys3d.set_target(body, desc)
end

local sdf_mesh -- 下で定義 (入れ子の木を平らにする)

-- handle の method (world:pose(key) / world:raycast(q, visitor) / debug_draw)
for _, k in ipairs({ "world", "world3d" }) do
	local m = lub.__refs[k]
	local P = k == "world" and phys2d or phys3d
	local forms = dual_forms(P)
	forms.set_target = k == "world" and set_target_2d or set_target_3d
	for name, fn in pairs(m) do
		m[name] = normalized(fn)
	end
	m.pose = normalized(forms.pose)
	m.raycast = normalized(forms.raycast)
	m.shape_cast = normalized(forms.shape_cast)
	m.joint = normalized(forms.joint)
	if P.debug then
		m.debug_draw = normalized(P.debug)
	end
	for _, bk in ipairs({
		k == "world" and "body" or "body3d",
		k == "world" and "shape" or "shape3d",
		k == "world" and "joint" or "joint3d",
	}) do
		local bm = lub.__refs[bk]
		if bm then
			for name, fn in pairs(bm) do
				bm[name] = normalized(fn)
			end
		end
	end
	local body = lub.__refs[k == "world" and "body" or "body3d"]
	body.set_target = normalized(forms.set_target)
	if forms.set_mass_data and P.set_mass_data then
		body.set_mass_data = normalized(forms.set_mass_data)
	end
	body.set_velocity = normalized(forms.set_velocity)
	if P.debug then
		m.debug_draw = P.debug
	end
end

Lub = lub
Gfx = compat(gfx, nil, { use_buffer = use_buffer, gfx_size = gfx.size })
local phys2d_forms = dual_forms(phys2d)
local phys3d_forms = dual_forms(phys3d)
phys2d_forms.set_target = set_target_2d
phys3d_forms.set_target = set_target_3d
Phys2d = compat(phys2d, "phys2d_", phys2d_forms, true)
Phys3d = compat(phys3d, "phys3d_", phys3d_forms, true)
Input = input
Profiler = compat(lub.profiler, nil, {
	profile_enabled = lub.profiler.enabled,
	profile_begin = lub.profiler.begin_scope,
	profile_end = lub.profiler.end_scope,
	profile_reset = lub.profiler.reset,
	profile_report = lub.profiler.report,
})
Mesh = compat(lub.mesh, nil, {
	sdf_mesh = function(...)
		return sdf_mesh(...)
	end,
})
Font = compat(lub.font, "font_")
Ui = compat(lub.ui, "ui_", { ui_begin = lub.ui.begin_window, ui_end = lub.ui.end_window })
Host = compat(lub.host, "host_")
Audio = compat(lub.audio, "audio_", { snd = audio_snd, audio_snd = audio_snd, snd_bytes = audio_snd })
Sys = compat(lub.sys, nil, { request_file = request_file })
Io = compat(io, "io_")
Png = compat(png, "png_", { PENDING = io.PENDING, READY = io.READY, ERROR = io.ERROR })
lub.Lub = lub
lub.Gfx = Gfx
lub.Phys2d = Phys2d
lub.Phys3d = Phys3d
lub.Input = Input
lub.Profiler = Profiler
lub.Mesh = Mesh
lub.Font = Font
lub.Ui = Ui
lub.Host = Host
lub.Audio = Audio
lub.Sys = Sys
lub.Io = Io
lub.Png = Png

-- ------------------------------------------------------- flat globals
-- 旧 Lua binding が global に置いていた名前。

local function expose(t, prefix)
	-- 互換 table は生成物の table を __index に持つので、両方を写す
	local ns = getmetatable(t) and getmetatable(t).__index or nil
	if ns then
		for name, fn in pairs(ns) do
			if type(fn) == "function" then
				_G[(prefix or "") .. name] = fn
			end
		end
	end
	for name, fn in pairs(t) do
		if type(fn) == "function" then
			_G[(prefix or "") .. name] = fn
		end
	end
end

expose(Gfx)
expose(Input)
expose(Audio)
expose(Host)
expose(Font)
expose(Ui)
expose(Io)
expose(Png)
expose(Phys2d)
expose(Phys3d)
expose(Profiler)
expose(Sys)
config = lub.config
quit = lub.quit
surface_nets = lub.mesh.surface_nets
-- body type の flat 定数 (旧 Lua binding が global に置いていた)
STATIC = phys2d.STATIC
KINEMATIC = phys2d.KINEMATIC
DYNAMIC = phys2d.DYNAMIC

-- ------------------------------------------------ Haxe の入れ子 sdf 木
-- lubx.Sdf (Haxe) は入れ子の table を渡す。生成した sdf_mesh は平らな node
-- 配列 (子は index) を受けるので、ここで平らにする。

local SDF_OPS = {
	sphere = { lub.mesh.SPHERE, { "r" }, 0 },
	box = { lub.mesh.BOX, { "hx", "hy", "hz" }, 0 },
	capsule = { lub.mesh.CAPSULE, { "ax", "ay", "az", "bx", "by", "bz", "r" }, 0 },
	torus = { lub.mesh.TORUS, { "rmajor", "rminor" }, 0 },
	move = { lub.mesh.MOVE, { "x", "y", "z" }, 1 },
	rotate = { lub.mesh.ROTATE, { "qx", "qy", "qz", "qw" }, 1 },
	scale = { lub.mesh.SCALE, { "s" }, 1 },
	mirror_x = { lub.mesh.MIRROR_X, {}, 1 },
	paint = { lub.mesh.PAINT, { "cr", "cg", "cb" }, 1 },
	bone = { lub.mesh.BONE, { "px", "py", "pz" }, 1 },
	union = { lub.mesh.UNION, {}, 2 },
	smin = { lub.mesh.SMIN, { "k" }, 2 },
	subtract = { lub.mesh.SUBTRACT, {}, 2 },
	ssub = { lub.mesh.SSUB, { "k" }, 2 },
	intersect = { lub.mesh.INTERSECT, {}, 2 },
}

local function sdf_flatten(nodes, t)
	local spec = SDF_OPS[t.op]
	if not spec then
		error("sdf_mesh: unknown op '" .. tostring(t.op) .. "'")
	end
	local params = {}
	for i, k in ipairs(spec[2]) do
		local v = t[k]
		if type(v) ~= "number" then
			error("sdf_mesh: node '" .. t.op .. "' needs number field '" .. k .. "'")
		end
		params[i] = v
	end
	if t.op == "paint" then
		params[4] = t.metallic or 0.0
		params[5] = t.roughness or 0.8
	end
	local node = { op = spec[1], a = -1, b = -1, params = params, name = t.name }
	if spec[3] == 1 then
		node.a = sdf_flatten(nodes, t.c)
	elseif spec[3] == 2 then
		node.a = sdf_flatten(nodes, t.a)
		node.b = sdf_flatten(nodes, t.b)
	end
	nodes[#nodes + 1] = node
	return #nodes - 1
end

local gen_sdf_mesh = lub.mesh.sdf_mesh
sdf_mesh = function(nodes, root, n, skin_k)
	if type(nodes) == "table" and nodes.root ~= nil then
		-- 入れ子形 { version = 1, root = node }: (tree, n, skin_k)
		local flat = {}
		local r = sdf_flatten(flat, nodes.root)
		return gen_sdf_mesh(flat, r, root, n)
	end
	return gen_sdf_mesh(nodes, root, n, skin_k)
end
_G.sdf_mesh = sdf_mesh
