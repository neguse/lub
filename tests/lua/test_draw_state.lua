-- lub.gfx.use_draw_state / draw_with_state (key で持つ draw の設定):
--   * draw_with_state は同じ設定の draw と同じ絵になる
--   * 同じ名前は draw ごとの bindings > draw state の固定 > pass の bindings の順
--     に勝つ (uniforms は member ごと、buffer は束縛の名前ごと)
--   * shader を作り直す (hot reload) と、次の draw_with_state で名前を結びつけ
--     直す (uniform の member の位置が変わっても正しく描く)
--   * version が同じなら opts も bindings も読まない (読めば error になる値でも)。
--     opts に nil を渡すと再主張だけ
--   * instance_count は draw と同じ (0 以下は描かない、省くと state の値か 1)
--   * 宣言の検査 (shader が無い、compute shader)
--   * 使われずに破棄された draw state の参照で描くと key を名指す error
-- 結果は offscreen の target を読み戻して画素で確かめる。
local M = {}

local W, H = 32, 4
local SWEEP = 8
local f = 0
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"
local rb
local shaders = {}
local states = {}
local step = 1
local requested = nil
local done_checks = {}
local sweep = { phase = 0 }
-- 作り直す shader の今の source と version
local hot = { version = 1 }

-- item[0] = (中心の x, _, 半幅, _)。高さは target 全体
local VS = [[
StructuredBuffer<float4> item;
static const float2 corner[6] = {
  float2(-1, -1), float2(1, -1), float2(1, 1),
  float2(-1, -1), float2(1, 1), float2(-1, 1),
};
struct VSOut {
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float4 r = item[0];
  float2 c = corner[vid];
  VSOut o;
  o.pos = float4(r.x + c.x * r.z, c.y, 0.0, 1.0);
  return o;
}
]]

-- 色は color + add
local FS = [[
struct F {
  float4 color;
  float4 add;
};
ConstantBuffer<F> f;
[shader("fragment")] float4 fs_main() : SV_Target {
  return float4((f.color + f.add).rgb, 1.0);
}
]]

-- 作り直した FS: color の前に member が増え、color の位置がずれる
local FS_MOVED = [[
struct F {
  float4 pad;
  float4 color;
  float4 add;
};
ConstantBuffer<F> f;
[shader("fragment")] float4 fs_main() : SV_Target {
  return float4((f.color + f.add + f.pad * 0.5).rgb, 1.0);
}
]]

local CS = [[
RWStructuredBuffer<float> dst;
[shader("compute")] [numthreads(1, 1, 1)] void cs_main(uint3 id : SV_DispatchThreadID) {
  dst[id.x] = 1.0;
}
]]

local function column_x(i)
	return -0.75 + i * 0.5
end
local function column_px(i)
	return i * 8 + 4
end

local RED = { 1, 0, 0, 1 }
local GREEN = { 0, 1, 0, 1 }
local BLUE = { 0, 0, 1, 1 }
local CYAN = { 0, 1, 1, 1 }
local YELLOW = { 1, 1, 0, 1 }
local BLACK = { 0, 0, 0, 1 }
local ZERO = { 0, 0, 0, 0 }

local function fail(message)
	print("DRAW_STATE_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function expect_error(what, needle, fn, ...)
	local ok, err = pcall(fn, ...)
	expect(not ok, what .. " must raise")
	expect(
		string.find(tostring(err), needle, 1, true) ~= nil,
		what .. ": the error must mention '" .. needle .. "': " .. tostring(err)
	)
end

local function item(i)
	return lub.gfx.transient_buffer(lub.gfx.STORAGE, { column_x(i), 0, 0.2, 0 })
end

local function opts(sh)
	return { shader = sh, depth = false, cull = lub.gfx.NONE }
end

local function target()
	return lub.gfx.use_texture("ds_rt", W, H, lub.gfx.RGBA8, nil, 1, { target = true })
end

local function begin(rt, bindings)
	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 }, bindings = bindings })
end

local steps = {
	{
		name = "draw_with_state draws like the equivalent draw",
		run = function(rt)
			states.a = lub.gfx.use_draw_state("ds_a", opts(shaders.main), { uniforms = { color = GREEN } }, 1)
			expect(states.a.key == "ds_a" and states.a.version == 1, "the ref must carry the key and version")
			begin(rt)
			lub.gfx.draw(6, { item = item(0), uniforms = { color = GREEN } }, opts(shaders.main))
			lub.gfx.draw_with_state(states.a, 6, { item = item(1) })
			-- method の形
			states.a:draw_with_state(6, { item = item(2) })
			lub.gfx.end_pass()
			return { GREEN, GREEN, GREEN, BLACK }
		end,
	},
	{
		name = "uniforms: per-draw > draw state > pass",
		run = function(rt)
			begin(rt, { uniforms = { color = RED, add = BLUE } })
			-- color は state、add は pass
			lub.gfx.draw_with_state(states.a, 6, { item = item(0) })
			-- color も add も draw
			lub.gfx.draw_with_state(states.a, 6, { item = item(1), uniforms = { color = YELLOW, add = ZERO } })
			-- 普通の draw は pass の上に draw
			lub.gfx.draw(6, { item = item(2), uniforms = { add = ZERO } }, opts(shaders.main))
			lub.gfx.end_pass()
			return { CYAN, YELLOW, RED, BLACK }
		end,
	},
	{
		name = "buffers: per-draw > draw state > pass",
		run = function(rt)
			local fixed = lub.gfx.use_buffer("ds_item2", lub.gfx.STORAGE, { column_x(2), 0, 0.2, 0 }, 1)
			states.b =
				lub.gfx.use_draw_state("ds_b", opts(shaders.main), { item = fixed, uniforms = { color = GREEN } }, 1)
			begin(rt, { item = item(3) })
			lub.gfx.draw_with_state(states.b, 6)
			lub.gfx.draw_with_state(states.b, 6, { item = item(0) })
			lub.gfx.draw(6, { uniforms = { color = BLUE } }, opts(shaders.main))
			lub.gfx.end_pass()
			return { GREEN, BLACK, GREEN, BLUE }
		end,
	},
	{
		name = "instance_count",
		run = function(rt)
			states.none = lub.gfx.use_draw_state(
				"ds_none",
				{ shader = shaders.main, depth = false, cull = lub.gfx.NONE, instance_count = 0 },
				{ uniforms = { color = RED } },
				1
			)
			begin(rt)
			-- state の 0 は描かない。draw の 1 は描く
			lub.gfx.draw_with_state(states.none, 6, { item = item(0) })
			lub.gfx.draw_with_state(states.none, 6, { item = item(1) }, 1)
			-- 省くと 1、0 以下は描かない
			lub.gfx.draw_with_state(states.a, 6, { item = item(2) })
			lub.gfx.draw_with_state(states.a, 6, { item = item(3) }, 0)
			lub.gfx.draw_with_state(states.a, 6, { item = item(3) }, -1)
			lub.gfx.end_pass()
			return { BLACK, RED, GREEN, BLACK }
		end,
	},
	{
		name = "draw state before the shader reload",
		run = function(rt)
			states.hot = lub.gfx.use_draw_state("ds_hot", opts(shaders.hot), { uniforms = { color = GREEN } }, 1)
			begin(rt)
			lub.gfx.draw_with_state(states.hot, 6, { item = item(0) })
			lub.gfx.end_pass()
			return { GREEN, BLACK, BLACK, BLACK }
		end,
	},
	{
		name = "draw state after the shader reload (color moved)",
		run = function(rt)
			-- 同じ key の shader を別の source で作り直す (handle は同じ)
			hot.fs, hot.version = FS_MOVED, 2
			local again = lub.gfx.use_shader("ds_hot_sh", VS, hot.fs, hot.version)
			expect(again.handle == shaders.hot.handle, "a recompiled shader must keep its handle")
			shaders.hot = again
			-- 同じ version の再主張: opts / bindings は読まない (固定の color はそのまま)
			local st = lub.gfx.use_draw_state("ds_hot", nil, nil, 1)
			expect(st and st.handle == states.hot.handle, "reasserting a draw state must return it")
			begin(rt)
			lub.gfx.draw_with_state(st, 6, { item = item(1) })
			-- 名前は作り直した shader に結びつく: pad は draw から
			lub.gfx.draw_with_state(st, 6, { item = item(2), uniforms = { pad = { 0, 0, 2, 0 } } })
			lub.gfx.end_pass()
			return { BLACK, GREEN, CYAN, BLACK }
		end,
	},
}

local function pixel(bytes, stride, x, y)
	local o = y * stride + x * 4
	return bytes[o + 1], bytes[o + 2], bytes[o + 3]
end

local function check_color(bytes, stride, x, col, what)
	for y = 0, H - 1 do
		local r, g, b = pixel(bytes, stride, x, y)
		local want = { col[1] * 255, col[2] * 255, col[3] * 255 }
		expect(
			math.abs(r - want[1]) <= 2 and math.abs(g - want[2]) <= 2 and math.abs(b - want[3]) <= 2,
			string.format(
				"%s: pixel (%d, %d) is (%d, %d, %d), want (%d, %d, %d)",
				what,
				x,
				y,
				r,
				g,
				b,
				want[1],
				want[2],
				want[3]
			)
		)
	end
end

local function run_steps()
	local rt = target()
	local request = nil
	if not requested then
		local s = steps[step]
		requested = { id = 100 + step, cols = s.run(rt) }
		request = requested.id
	end
	local status, bytes, _, _, _, stride, id = rb:read_texture(rt, request)
	expect(status ~= "error", "readback failed")
	if status == "ready" and id == requested.id then
		local s = steps[step]
		for i = 0, 3 do
			check_color(bytes, stride, column_px(i), requested.cols[i + 1], s.name)
		end
		done_checks[#done_checks + 1] = s.name
		step = step + 1
		requested = nil
	end
	expect(f < 400, "readback for step " .. step .. " did not arrive")
	return step > #steps
end

-- 宣言の検査と、version が同じなら opts / bindings を読まないこと
local function check_declarations()
	expect_error("a draw state without a shader", "opts.shader is required", lub.gfx.use_draw_state, "ds_bad", {}, nil)
	expect_error(
		"a draw state with a compute shader",
		"compute shader",
		lub.gfx.use_draw_state,
		"ds_bad",
		{ shader = shaders.cs },
		nil
	)
	-- 読めば error になる opts / bindings でも、同じ version なら読まない
	local bad_opts = { shader = 12345 }
	local bad_bindings = { item = 3 }
	local hit = lub.gfx.use_draw_state("ds_a", bad_opts, bad_bindings, 1)
	expect(hit and hit.handle == states.a.handle, "a version hit must return the draw state without reading")
	expect_error("reading bad opts on a miss", "shader expected", lub.gfx.use_draw_state, "ds_a", bad_opts, nil, 2)
	expect_error(
		"reading bad bindings on a miss",
		"buffer or texture expected",
		lub.gfx.use_draw_state,
		"ds_a",
		opts(shaders.main),
		bad_bindings,
		2
	)
	-- opts に nil は再主張だけ
	local again = lub.gfx.use_draw_state("ds_a", nil, nil, 1)
	expect(again and again.handle == states.a.handle, "reasserting with nil opts must return the draw state")
	local missing, why = lub.gfx.use_draw_state("ds_missing", nil, nil, 1)
	expect(missing == nil and why == "not found", "reasserting an unknown key must return nil, 'not found'")
	expect(lub.gfx.lookup_draw_state("ds_a").handle == states.a.handle, "lookup_draw_state must find the key")
	expect(lub.gfx.lookup_draw_state("ds_missing") == nil, "lookup_draw_state of an unknown key is nil")
end

-- 使われずに破棄された draw state: 参照で描くと key を名指す error、宣言し直せば
-- 古い参照もそれを指す
local function sweep_phase()
	local rt = target()
	if sweep.phase == 0 then
		sweep.ref = lub.gfx.use_draw_state("ds_swept", opts(shaders.main), nil, 1)
		sweep.from = f
		sweep.phase = 1
	elseif sweep.phase == 1 and f - sweep.from > SWEEP + 1 then
		expect(lub.gfx.lookup_draw_state("ds_swept") == nil, "an unused draw state must be swept")
		begin(rt)
		expect_error(
			"drawing with a swept draw state",
			"'ds_swept' was swept",
			lub.gfx.draw_with_state,
			sweep.ref,
			6,
			{ item = item(0) }
		)
		lub.gfx.end_pass()
		local again = lub.gfx.use_draw_state("ds_swept", opts(shaders.main), nil, 1)
		expect(again.handle ~= sweep.ref.handle, "a swept key must get a new handle")
		begin(rt)
		lub.gfx.draw_with_state(sweep.ref, 6, { item = item(0) })
		lub.gfx.end_pass()
		return true
	end
	return false
end

function M.on_init()
	lub.config({ backend = backend, width = 64, height = 64, resource_sweep_after_frames = SWEEP })
	rb = lub.gfx.readback("ds_rb")
end

function M.on_frame()
	f = f + 1
	shaders.main = lub.gfx.use_shader("ds_main", VS, FS, 1)
	shaders.cs = lub.gfx.use_shader_compute("ds_cs", CS, 1)
	shaders.hot = lub.gfx.use_shader("ds_hot_sh", VS, hot.fs or FS, hot.version)
	-- 描かない frame も draw state を使ったことにする (sweep で消えないように)
	for _, key in ipairs({ "ds_a", "ds_b", "ds_none", "ds_hot" }) do
		lub.gfx.use_draw_state(key, nil, nil, 1)
	end
	if step <= #steps then
		if run_steps() then
			check_declarations()
		end
		return
	end
	if sweep_phase() then
		print("DRAW_STATE_OK checks=" .. #done_checks)
		lub.quit()
	end
end

return M
