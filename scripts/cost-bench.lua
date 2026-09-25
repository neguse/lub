-- 操作ごとのコストの目安を測る raw Lua の entry (manual の「コストの目安と
-- ホットパス」章の表)。repo root から起動し、表示し終えると自分で終了する:
--   lub scripts/cost-bench.lua
--   scripts/run-headless.sh ./build-release-linux/lub scripts/cost-bench.lua
-- backend は LUB_BACKEND で選ぶ。
--
-- 各行は tcs が C# から生成するのと同じ形の Lua を n 回まわし、かかった時間
-- (os.clock) と確保 (GC を止めて数える) を 1 回あたりに直す。REPS 回測って
-- 一番速い値を出す。GPU を使う行は 3 フレームに 1 回ずつ測り、前のフレームの
-- GPU の仕事と重ならないようにする (最初の一巡は慣らしで数えない)。計算の行は
-- 最後のフレームでまとめて測る (GC を止めて大量に作ったオブジェクトが、先に
-- 測る行の heap を変えないように)。
local lubx = require("lubx")

local M = {}
local N, REPS, LIST = 200000, 5, 256
local DRAWS, ITEMS, BIG = 2000, 10000, 16384

-- tcs が TryGetValue を移す先 (Dict.TryGet) と同じ形
local function try_get(d, k, default)
	local v = d[k]
	if v ~= nil then
		return true, v
	end
	return false, default
end

local function use(v)
	return v
end

-- 引数の中の三項演算子 (クロージャになる) と、if の文にした形
local function pick_closure(c, a, b)
	return use((function()
		if c then
			return a
		end
		return b
	end)())
end

local function pick_if(c, a, b)
	local v = b
	if c then
		v = a
	end
	return use(v)
end

-- && の中の TryGetValue (クロージャになる) と、文に分けた形
local function positive_closure(d, k)
	local v
	return (function()
		local found, got = try_get(d, k, 0)
		v = got
		return found
	end)() and v > 0
end

local function positive_if(d, k)
	local found, v = try_get(d, k, 0)
	return found and v > 0
end

-- 引数の中のクラスのオブジェクト初期化子 (クロージャになる) と、local 変数に
-- 入れてから渡す形
local function init_closure(x, y, z)
	return use((function()
		local o = Vec3.new(0.0, 0.0, 0.0)
		o.x = x
		o.y = y
		o.z = z
		return o
	end)())
end

local function init_local(x, y, z)
	local o = Vec3.new(0.0, 0.0, 0.0)
	o.x = x
	o.y = y
	o.z = z
	return use(o)
end

-- 測る Lua から見える値 (g は GPU を使う行のリソース)
local E = {
	a = Vec3.new(1, 2, 3),
	b = Vec3.new(4, 5, 6),
	c = Vec3.new(0, 0, 0),
	m1 = Mat4.identity(),
	m2 = Mat4.identity(),
	m3 = Mat4.identity(),
	kept = {},
	dict = { hp = 3 },
	g = {},
	pick_closure = pick_closure,
	pick_if = pick_if,
	positive_closure = positive_closure,
	positive_if = positive_if,
	init_closure = init_closure,
	init_local = init_local,
}
for k = 1, LIST do
	E.kept[k] = 0.0
end
local g = E.g

local PRELUDE = "local E = ...\n"
	.. "local a, b, c, m1, m2, m3, kept, dict, g = E.a, E.b, E.c, E.m1, E.m2, E.m3, E.kept, E.dict, E.g\n"
	.. "local pick_closure, pick_if = E.pick_closure, E.pick_if\n"
	.. "local positive_closure, positive_if = E.positive_closure, E.positive_if\n"
	.. "local init_closure, init_local = E.init_closure, E.init_local\n"
	.. string.format("local LIST, ITEMS, BIG = %d, %d, %d\n", LIST, ITEMS, BIG)

local SPRITE = "g.batch:sprite_color(g.atlas, g.rect, i % 600, 10, 4, 4, 1.0, 0.0, 1, 0.5, 0, 1)"
local INST = "g.inst:add(i * 0.01, 0, 0, 1, 0, 0, 0, 1, 1, 1, 1, 1)"

-- { 名前, 1 回分の Lua (i は 1..n), n = 回数 (既定 N), per = 1 回の要素数,
--   locals = ループの前に置く local, pre = 測る前に走らせる Lua,
--   pass = "pass" (pass の中) / "frame" (pass の bindings にフレームの定数) }
local cpu_rows = {
	{ "table {x=,y=,z=}", "local _ = { x = 1.0, y = 2.0, z = 3.0 }" },
	{ "Vec3.new (tcs class)", "local _ = Vec3.new(1.0, 2.0, 3.0)" },
	{ "a + b (Vec3)", "local _ = a + b" },
	{ "c:add_in_place(b)", "c:add_in_place(b)" },
	{ "a + b * 0.5 (Vec3)", "local _ = a + b * 0.5" },
	{ "c:add_scaled_in_place(b, 0.5)", "c:add_scaled_in_place(b, 0.5)" },
	{
		"scalar locals x3",
		"x, y, z = x + vx * dt, y + vy * dt, z + vz * dt",
		locals = "local vx, vy, vz, dt, x, y, z = 4.0, 5.0, 6.0, 0.5, 0.0, 0.0, 0.0",
	},
	{ "m1 * m2 (Mat4)", "local _ = m1 * m2", n = N // 4 },
	{ "m3:set_mul(m1, m2)", "m3:set_mul(m1, m2)", n = N // 4 },
	{
		"new list + table.insert (per elem)",
		"local t = {} for k = 1, LIST do table.insert(t, 1.0) end",
		n = N // LIST,
		per = LIST,
	},
	{ "new table + t[k] = x (per elem)", "local t = {} for k = 1, LIST do t[k] = 1.0 end", n = N // LIST, per = LIST },
	{
		"List.Clear() + table.insert (per elem)",
		"(function() local o = kept for k in pairs(o) do o[k] = nil end end)()"
			.. " for k = 1, LIST do table.insert(kept, 1.0) end",
		n = N // LIST,
		per = LIST,
	},
	{ "kept list, t[i] = x (per elem)", "for k = 1, LIST do kept[k] = 1.0 end", n = N // LIST, per = LIST },
	{ "ternary in an argument (closure)", "pick_closure(i > 5, 1.0, 2.0)" },
	{ "ternary as if", "pick_if(i > 5, 1.0, 2.0)" },
	{ "TryGetValue in && (closure)", "positive_closure(dict, 'hp')" },
	{ "TryGetValue as statements", "positive_if(dict, 'hp')" },
	{ "object initializer in an argument (closure)", "init_closure(1.0, 2.0, 3.0)", n = N // 4 },
	{ "object initializer in a local", "init_local(1.0, 2.0, 3.0)", n = N // 4 },
}

local DRAW = "g.model[13] = i * 1e-6 "
local gpu_rows = {
	{
		"use_buffer, same version, 16384 floats",
		"lub.gfx.use_buffer('cost_big', lub.gfx.STORAGE, g.big, 1)",
		n = 1000,
	},
	{ "use_buffer, new data (per float)", "lub.gfx.use_buffer(g.keys[i], lub.gfx.STORAGE, g.big)", n = 8, per = BIG },
	{
		"draw, new tables per draw",
		DRAW
			.. "lub.gfx.draw(3, { verts = g.vb, uniforms = { model = g.model, view_proj = g.vp,"
			.. " light_dir = g.light, tint = g.tint } }, { shader = g.sh, depth = false, cull = lub.gfx.NONE })",
		n = DRAWS,
		pass = "pass",
	},
	{ "draw, reused tables", DRAW .. "lub.gfx.draw(3, g.all_b, g.opts)", n = DRAWS, pass = "pass" },
	{
		"draw, frame constants in pass bindings",
		DRAW .. "lub.gfx.draw(3, g.model_b, g.opts)",
		n = DRAWS,
		pass = "frame",
	},
	{
		"draw_with_state + pass bindings",
		DRAW .. "lub.gfx.draw_with_state(g.state, 3, g.state_b)",
		n = DRAWS,
		pass = "frame",
		pre = "g.state = lub.gfx.use_draw_state('cost_state', g.opts, { verts = g.vb }, 1)",
	},
	{
		"transient_buffer, 16 floats (per call)",
		"lub.gfx.transient_buffer(lub.gfx.STORAGE, g.small, 16)",
		n = 1000,
		pass = "pass",
	},
	{
		"transient_buffer, 16384 floats (per float)",
		"lub.gfx.transient_buffer(lub.gfx.STORAGE, g.big, BIG)",
		n = 8,
		per = BIG,
		pass = "pass",
	},
	{ "SpriteBatch:sprite_color (per sprite)", SPRITE, n = ITEMS, pre = "g.batch:begin()", pass = "pass" },
	{
		"SpriteBatch:flush (per sprite)",
		"g.batch:flush()",
		n = 1,
		per = ITEMS,
		pre = "g.batch:begin() for i = 1, ITEMS do " .. SPRITE .. " end",
		pass = "pass",
	},
	{ "InstanceBatch3d:add (per instance)", INST, n = ITEMS, pre = "g.inst:begin()", pass = "pass" },
	{
		"InstanceBatch3d:upload (per instance)",
		"g.inst:upload()",
		n = 1,
		per = ITEMS,
		pre = "g.inst:begin() for i = 1, ITEMS do " .. INST .. " end",
		pass = "pass",
	},
}

local function compile(row)
	local src = PRELUDE
		.. "return function() "
		.. (row.pre or "")
		.. " end, function(n) "
		.. (row.locals or "")
		.. " for i = 1, n do "
		.. row[2]
		.. " end end"
	row.pre_fn, row.loop = assert(load(src, "=" .. row[1]))(E)
	row.n, row.per = row.n or N, row.per or 1
end

-- 1 回測って、1 回あたりの時間 (ns) と確保 (byte) を返す。gc_on なら GC を
-- 止めずにまわす (ごみの後始末の分が時間に足される)
local function run(row, gc_on)
	collectgarbage("collect")
	row.pre_fn()
	if not gc_on then
		collectgarbage("stop")
	end
	local kb0 = collectgarbage("count")
	local t0 = os.clock()
	row.loop(row.n)
	local t1 = os.clock()
	local kb1 = collectgarbage("count")
	collectgarbage("restart")
	local ops = row.n * row.per
	return (t1 - t0) * 1e9 / ops, (kb1 - kb0) * 1024 / ops
end

local function report(row)
	print(string.format("COST %-44s %9.1f ns %8.1f B", row[1], row.best, row.bytes))
end

local function keep_best(row, ns, bytes)
	row.best, row.bytes = math.min(row.best or math.huge, ns), bytes
end

local function cpu_benches()
	for _, row in ipairs(cpu_rows) do
		compile(row)
	end
	-- GC を止めずにまわすと、ごみの後始末 (incremental GC) の分が足される
	local gc_on = {}
	for i = 1, 2 do
		gc_on[i] = math.huge
		for _ = 1, REPS do
			gc_on[i] = math.min(gc_on[i], (run(cpu_rows[i], true)))
		end
	end
	for _, row in ipairs(cpu_rows) do
		for _ = 1, REPS do
			keep_best(row, run(row))
		end
		report(row)
	end
	for i = 1, 2 do
		print(string.format("COST   %-40s %9.1f ns with GC running", cpu_rows[i][1], gc_on[i]))
	end
end

local VS = [[
struct U { float4x4 model; float4x4 view_proj; float4 light_dir; float4 tint; };
ConstantBuffer<U> u;
StructuredBuffer<float4> verts;
struct VSOut { float4 c : COLOR0; float4 pos : SV_Position; };
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  VSOut o;
  o.pos = mul(u.view_proj, mul(u.model, float4(verts[vid].xyz, 1.0)));
  o.c = u.tint + u.light_dir * 0.0;
  return o;
}
]]
local FS = [[
struct FSIn { float4 c : COLOR0; };
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target { return i.c; }
]]

local function ident(s)
	return { s, 0, 0, 0, 0, s, 0, 0, 0, 0, s, 0, 0, 0, 0, 1 }
end

local function setup()
	g.sh = lub.gfx.use_shader("cost_sh", VS, FS, 1)
	g.vb = lub.gfx.use_buffer("cost_vb", lub.gfx.STORAGE, { 0, 0, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1 }, 1)
	if g.sh == nil or g.vb == nil then
		return false
	end
	if not g.model then
		g.model, g.vp, g.light, g.tint = ident(0.001), ident(1), { 0, 1, 0, 0 }, { 1, 1, 1, 1 }
		g.opts = { shader = g.sh, depth = false, cull = lub.gfx.NONE }
		g.all_b = { verts = g.vb, uniforms = { model = g.model, view_proj = g.vp, light_dir = g.light, tint = g.tint } }
		g.model_b = { verts = g.vb, uniforms = { model = g.model } }
		g.state_b = { uniforms = { model = g.model } }
		g.frame_b = { uniforms = { view_proj = g.vp, light_dir = g.light, tint = g.tint } }
		g.small, g.big, g.keys = {}, {}, {}
		for i = 1, 16 do
			g.small[i] = 1.0
		end
		for i = 1, BIG do
			g.big[i] = i * 0.25
		end
		for k = 1, 8 do
			g.keys[k] = "cost_up" .. k
		end
		g.batch = lubx.SpriteBatch.new(640, 480, nil, "cost_sb", true)
		g.atlas, g.rect = lubx.SpriteBatch.ensure_white_atlas(), lubx.Rect.new(0, 0, 4, 4)
		g.inst = lubx.InstanceBatch3d.new()
		for _, row in ipairs(gpu_rows) do
			compile(row)
		end
	end
	g.opts.shader = g.sh
	return true
end

local frame = 0

function M.on_init()
	lub.config({ width = 64, height = 64 })
end

local function step()
	frame = frame + 1
	if not setup() or frame % 3 ~= 0 then
		return
	end
	local k = frame // 3 - 1
	local round, row = k // #gpu_rows, gpu_rows[k % #gpu_rows + 1]
	if round > REPS then
		cpu_benches()
		print(string.format("COST backend=%s, %d draws per frame", os.getenv("LUB_BACKEND") or "default", DRAWS))
		for _, r in ipairs(gpu_rows) do
			report(r)
		end
		os.exit(0, true)
	end
	if row.pass then
		local pass = { target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } }
		if row.pass == "frame" then
			pass.bindings = g.frame_b
		end
		lub.gfx.begin_pass(pass)
	end
	local ns, bytes = run(row)
	if row.pass then
		lub.gfx.end_pass()
	end
	if round > 0 then
		keep_best(row, ns, bytes)
	end
end

function M.on_frame()
	local ok, err = pcall(step)
	if not ok then
		print("COST error: " .. tostring(err))
		os.exit(1, true)
	end
end

return M
