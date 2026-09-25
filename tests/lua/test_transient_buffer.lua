-- lub.gfx.transient_buffer (この frame の間だけ使う buffer):
--   * 1 つの pass で draw ごとに別の transient を束縛すると、どの draw も自分の
--     data を読む (記録した順)。data は呼んだ時点で写すので、同じ table を
--     書き換えて使い回してよい
--   * count は data の先頭から使う要素数 (table と view の両方)。shader から
--     見た StructuredBuffer の長さ (GetDimensions) も count になる。use_buffer
--     の count も同じ (SDL3 GPU は buffer 全体を束縛するので、大きさが変わった
--     key は確保した大きさが見える)
--   * INDEX の transient で indexed draw、整数列 (transient_buffer_ints) も使える
--   * dispatch では読むだけの StructuredBuffer に使え、RWStructuredBuffer は error
--   * 前の frame の transient を束縛すると error、on_init では作れない
-- 結果は offscreen の target を読み戻して画素で確かめる。
local M = {}

local W, H = 32, 4
local f = 0
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"
local rb
local init_error
local shaders = {}
local stale = nil
local step = 1
local requested = nil
local done_checks = {}

-- item[0] = (中心の x, _, 半幅, _)、item[1] = 色。高さは target 全体。
local VS = [[
StructuredBuffer<float4> item;
static const float2 corner[6] = {
  float2(-1, -1), float2(1, -1), float2(1, 1),
  float2(-1, -1), float2(1, 1), float2(-1, 1),
};
struct VSOut {
  float4 col : COLOR0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float4 r = item[0];
  float2 c = corner[vid];
  VSOut o;
  o.col = float4(item[1].xyz, 1.0);
  o.pos = float4(r.x + c.x * r.z, c.y, 0.0, 1.0);
  return o;
}
]]

-- indexed draw 用: 頂点 id は index の値 (4 隅)
local VS_INDEXED = [[
StructuredBuffer<float4> item;
static const float2 corner[4] = {
  float2(-1, -1), float2(1, -1), float2(1, 1), float2(-1, 1),
};
struct VSOut {
  float4 col : COLOR0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float4 r = item[0];
  float2 c = corner[vid];
  VSOut o;
  o.col = float4(item[1].xyz, 1.0);
  o.pos = float4(r.x + c.x * r.z, c.y, 0.0, 1.0);
  return o;
}
]]

-- item[0] で列に置き、色の赤は probe の要素数 (float4 の数) の 8 倍 / 255
local VS_DIMS = [[
StructuredBuffer<float4> item;
StructuredBuffer<float4> probe;
static const float2 corner[6] = {
  float2(-1, -1), float2(1, -1), float2(1, 1),
  float2(-1, -1), float2(1, 1), float2(-1, 1),
};
struct VSOut {
  float4 col : COLOR0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float4 r = item[0];
  uint n, stride;
  probe.GetDimensions(n, stride);
  float2 c = corner[vid];
  VSOut o;
  o.col = float4(float(n) * 8.0 / 255.0, 0.0, 0.0, 1.0);
  o.pos = float4(r.x + c.x * r.z, c.y, 0.0, 1.0);
  return o;
}
]]

local FS = [[
struct FSIn {
  float4 col : COLOR0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return i.col;
}
]]

local CS = [[
StructuredBuffer<float> src;
RWStructuredBuffer<float> dst;
[shader("compute")] [numthreads(8, 1, 1)] void cs_main(uint3 id : SV_DispatchThreadID) {
  dst[id.x] = src[id.x] * 2.0 + 1.0;
}
]]

-- 列 i (0..3) の中心の x (NDC) と、その列の中の画素の x
local function column_x(i)
	return -0.75 + i * 0.5
end
local function column_px(i)
	return i * 8 + 4
end

local RED = { 1, 0, 0 }
local GREEN = { 0, 1, 0 }
local BLUE = { 0, 0, 1 }
local WHITE = { 1, 1, 1 }
local YELLOW = { 1, 1, 0 }
local BLACK = { 0, 0, 0 }

local function fail(message)
	print("TRANSIENT_BUFFER_FAIL frame " .. f .. ": " .. message)
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

-- item の 8 float (中心 x、半幅、色) を t の先頭に書く (table は使い回す)
local function write_item(t, x, half, col)
	t[1], t[2], t[3], t[4] = x, 0, half, 0
	t[5], t[6], t[7], t[8] = col[1], col[2], col[3], 0
end

-- 6 頂点の quad。indices を渡すと indexed draw (頂点 id は index の値)
local function draw(item, indices)
	lub.gfx.draw(6, { item = item, indices = indices }, {
		shader = indices and shaders.indexed or shaders.draw,
		depth = false,
		cull = lub.gfx.NONE,
	})
end

-- 列 i に probe の要素数を描く
local function draw_dims(i, probe)
	local t = {}
	write_item(t, column_x(i), 0.2, BLACK)
	lub.gfx.draw(6, { item = lub.gfx.transient_buffer(lub.gfx.STORAGE, t), probe = probe }, {
		shader = shaders.dims,
		depth = false,
		cull = lub.gfx.NONE,
	})
end

-- 要素数 n (float4 の数) を描いた列の色
local function dims_color(n)
	return { n * 8 / 255, 0, 0 }
end

-- 大きさが変わった key の buffer の見える要素数: SDL3 GPU は buffer 全体
-- (確保した cap 個)、他の backend は data の分 (n 個)
local function keyed_dims(n, cap)
	return dims_color(backend == "sdlgpu" and cap or n)
end

local function target()
	return lub.gfx.use_texture("tb_rt", W, H, lub.gfx.RGBA8, nil, 1, { target = true })
end

-- 各 step は target に描き、読み戻した画素で確かめる列の色を返す
local steps = {
	{
		name = "record order (one pass, a transient per draw, reused table + count)",
		run = function(rt)
			-- 使い回す table は 12 要素。先頭 8 個だけを count で渡す
			local scratch = { 0, 0, 0, 0, 0, 0, 0, 0, 9, 9, 9, 9 }
			local colors = { RED, GREEN, BLUE, WHITE }
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			for i = 0, 3 do
				write_item(scratch, column_x(i), 0.2, colors[i + 1])
				local ref = lub.gfx.transient_buffer(lub.gfx.STORAGE, scratch, 8)
				expect(ref.handle < -1, "a transient handle must not look like a resource handle")
				expect(not lub.gfx.resource_info(ref.handle), "a transient has no key")
				draw(ref)
			end
			lub.gfx.end_pass()
			return { RED, GREEN, BLUE, WHITE }, { [8] = BLACK, [16] = BLACK }
		end,
	},
	{
		name = "count on a view and on use_buffer",
		run = function(rt)
			-- interleave_pn の view: 1 頂点 8 float (pos, pad, nrm, pad)。先頭の
			-- 1 頂点分だけを count で使う (pos = 中心 x, _, 半幅、nrm = 色)
			local view = lub.io.interleave_pn({
				positions = { column_x(1), 0, 0.2, column_x(3), 0, 0.2 },
				normals = { 0, 0, 1, 1, 0, 0 },
				vert_count = 2,
			})
			expect(#view == 16, "interleave_pn must return 16 floats")
			local from_view = lub.gfx.transient_buffer(lub.gfx.STORAGE, view, 8)
			-- use_buffer の count (version の hit でも同じ handle)
			local t = {}
			write_item(t, column_x(2), 0.2, GREEN)
			t[9], t[10], t[11], t[12] = column_x(0), 0, 0.9, 0
			local keyed = lub.gfx.use_buffer("tb_counted", lub.gfx.STORAGE, t, 1, 8)
			local again = lub.gfx.use_buffer("tb_counted", lub.gfx.STORAGE, t, 1, 8)
			expect(again.handle == keyed.handle, "a version hit with count must return the same buffer")
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw(from_view)
			draw(keyed)
			lub.gfx.end_pass()
			return { BLACK, BLUE, GREEN, BLACK }
		end,
	},
	{
		name = "count sets the length the shader sees",
		run = function(rt)
			local t16, ints = {}, {}
			for i = 1, 16 do
				t16[i] = i
				ints[i] = i
			end
			local view = lub.io.interleave_pn({
				positions = { 1, 2, 3, 4, 5, 6 },
				normals = { 1, 2, 3, 4, 5, 6 },
				vert_count = 2,
			})
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw_dims(0, lub.gfx.transient_buffer(lub.gfx.STORAGE, t16, 8))
			draw_dims(1, lub.gfx.transient_buffer(lub.gfx.STORAGE, view, 12))
			draw_dims(2, lub.gfx.transient_buffer_ints(lub.gfx.STORAGE, ints, 4))
			-- 初めての確保はちょうどの大きさ (どの backend も count の分)
			draw_dims(3, lub.gfx.use_buffer("tb_dims", lub.gfx.STORAGE, t16, nil, 12))
			lub.gfx.end_pass()
			return { dims_color(2), dims_color(3), dims_color(1), dims_color(3) }
		end,
	},
	{
		name = "count on a keyed buffer that grows",
		run = function(rt)
			local t16 = {}
			for i = 1, 16 do
				t16[i] = i
			end
			-- 12 から 16 個に伸びて作り直す (確保は 256 byte = float4 16 個)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw_dims(1, lub.gfx.use_buffer("tb_dims", lub.gfx.STORAGE, t16, nil, 16))
			lub.gfx.end_pass()
			return { BLACK, keyed_dims(4, 16), BLACK, BLACK }
		end,
	},
	{
		name = "count on a keyed buffer shrunk in place",
		run = function(rt)
			local t16 = {}
			for i = 1, 16 do
				t16[i] = i
			end
			-- 16 から 8 個に縮む。確保した 256 byte のまま書き込む
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw_dims(2, lub.gfx.use_buffer("tb_dims", lub.gfx.STORAGE, t16, nil, 8))
			lub.gfx.end_pass()
			return { BLACK, BLACK, keyed_dims(2, 16), BLACK }
		end,
	},
	{
		name = "INDEX transients and integer data",
		run = function(rt)
			-- 整数の STORAGE (float に写す): target 全体を黄色に
			local bg = lub.gfx.transient_buffer_ints(lub.gfx.STORAGE, { 0, 0, 1, 0, 1, 1, 0, 0 })
			local quad_f = lub.gfx.transient_buffer(lub.gfx.INDEX, { 0, 1, 2, 0, 2, 3, 7, 7 }, 6)
			local quad_i = lub.gfx.transient_buffer_ints(lub.gfx.INDEX, { 0, 1, 2, 0, 2, 3 })
			local t = {}
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw(bg)
			write_item(t, column_x(1), 0.2, RED)
			draw(lub.gfx.transient_buffer(lub.gfx.STORAGE, t), quad_f)
			write_item(t, column_x(3), 0.2, BLUE)
			draw(lub.gfx.transient_buffer(lub.gfx.STORAGE, t), quad_i)
			lub.gfx.end_pass()
			return { YELLOW, RED, YELLOW, BLUE }
		end,
	},
	{
		name = "dispatch reads a transient; RW use is rejected",
		run = function(rt)
			-- dst = src * 2 + 1 が item になる: 列 2 に青
			local want = { column_x(2), 0, 0.2, 0, 0, 0, 1, 0 }
			local src = {}
			for i = 1, 8 do
				src[i] = (want[i] - 1) / 2
			end
			local input = lub.gfx.transient_buffer(lub.gfx.STORAGE, src)
			local dst = lub.gfx.use_buffer_empty("tb_dst", lub.gfx.STORAGE, 8, 1)
			lub.gfx.dispatch(1, 1, 1, { src = input, dst = dst }, { shader = shaders.cs })
			expect_error(
				"binding a transient to a RWStructuredBuffer",
				"read-only",
				lub.gfx.dispatch,
				1,
				1,
				1,
				{ src = input, dst = input },
				{ shader = shaders.cs }
			)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw(dst)
			lub.gfx.end_pass()
			return { BLACK, BLACK, BLUE, BLACK }
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
		local cols, extra = s.run(rt)
		requested = { id = 100 + step, cols = cols, extra = extra or {} }
		request = requested.id
	end
	local status, bytes, _, _, _, stride, id = rb:read_texture(rt, request)
	expect(status ~= "error", "readback failed")
	if status == "ready" and id == requested.id then
		local s = steps[step]
		for i = 0, 3 do
			check_color(bytes, stride, column_px(i), requested.cols[i + 1], s.name)
		end
		for x, col in pairs(requested.extra) do
			check_color(bytes, stride, x, col, s.name)
		end
		done_checks[#done_checks + 1] = s.name
		step = step + 1
		requested = nil
	end
	expect(f < 300, "readback for step " .. step .. " did not arrive")
	return step > #steps
end

function M.on_init()
	lub.config({ backend = backend, width = 64, height = 64 })
	rb = lub.gfx.readback("tb_rb")
	local ok, err = pcall(lub.gfx.transient_buffer, lub.gfx.STORAGE, { 1, 2, 3, 4 })
	init_error = ok and "no error" or tostring(err)
end

function M.on_frame()
	f = f + 1
	if f == 1 then
		expect(
			string.find(init_error, "inside a frame", 1, true) ~= nil,
			"transient_buffer in on_init must raise: " .. init_error
		)
		shaders.draw = lub.gfx.use_shader("tb_draw", VS, FS, 1)
		shaders.indexed = lub.gfx.use_shader("tb_indexed", VS_INDEXED, FS, 1)
		shaders.cs = lub.gfx.use_shader_compute("tb_cs", CS, 1)
		shaders.dims = lub.gfx.use_shader("tb_dims_sh", VS_DIMS, FS, 1)

		-- 引数の検査
		expect_error("count past the end", "out of range", lub.gfx.transient_buffer, lub.gfx.STORAGE, { 1, 2 }, 3)
		expect_error("a negative count", "out of range", lub.gfx.transient_buffer, lub.gfx.STORAGE, { 1, 2 }, -1)
		expect_error("count 0", "empty data", lub.gfx.transient_buffer, lub.gfx.STORAGE, { 1, 2 }, 0)
		expect_error("an empty table", "empty data", lub.gfx.transient_buffer, lub.gfx.STORAGE, {})
		expect_error("a UNIFORM transient", "INDEX/STORAGE", lub.gfx.transient_buffer, lub.gfx.UNIFORM, { 1 })
		expect_error(
			"use_buffer count past the end",
			"out of range",
			lub.gfx.use_buffer,
			"tb_bad",
			lub.gfx.STORAGE,
			{ 1, 2 },
			nil,
			3
		)
		expect_error(
			"use_buffer count 0",
			"empty data",
			lub.gfx.use_buffer,
			"tb_bad",
			lub.gfx.STORAGE,
			{ 1, 2 },
			nil,
			0
		)

		-- 次の frame で使う (前の frame の transient は error になる)
		local t = {}
		write_item(t, 0, 1, RED)
		stale = lub.gfx.transient_buffer(lub.gfx.STORAGE, t)
		return
	end
	if f == 2 then
		lub.gfx.begin_pass({ target = target(), clear_color = { 0, 0, 0, 1 } })
		expect_error("a transient from the previous frame", "earlier frame", draw, stale)
		lub.gfx.end_pass()
		return
	end
	if run_steps() then
		print("TRANSIENT_BUFFER_OK checks=" .. #done_checks)
		lub.quit()
	end
end

return M
