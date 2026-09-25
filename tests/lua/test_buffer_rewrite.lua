-- key で宣言する buffer を書き直したときの draw:
--   * 1 つの pass で同じ key を 3 回書き直し、そのたびに draw すると、どの
--     draw も書き直した時点の内容を読む (記録した順)。同じ大きさでも、
--     大きさを変えても同じ
--   * frame ごとに大きさを変えても (確保量に収まる伸び縮み、収まらない伸び、
--     大きく縮む) 正しく描け、参照の handle は変わらない
-- 結果は offscreen の target を読み戻して画素で確かめる。WebGPU は同じ大きさの
-- 書き直しがまだ最後の内容になる (native の backend で走らせる)。
local M = {}

local W, H = 32, 4
local f = 0
local rb
local shader
local step = 1
local requested = nil
local handle = nil

-- instance i が item[2i] (中心の x, _, 半幅, _) と item[2i+1] (色) を読む。
-- 高さは target 全体。
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
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID, uint iid : LUB_INSTANCE_ID) {
  float4 r = item[iid * 2];
  float2 c = corner[vid];
  VSOut o;
  o.col = float4(item[iid * 2 + 1].xyz, 1.0);
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

local RED = { 1, 0, 0 }
local GREEN = { 0, 1, 0 }
local BLUE = { 0, 0, 1 }
local WHITE = { 1, 1, 1 }
local BLACK = { 0, 0, 0 }
local COLORS = { RED, GREEN, BLUE, WHITE }

local function column_x(i)
	return -0.75 + i * 0.5
end

local function fail(message)
	print("BUFFER_REWRITE_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

-- 列 cols[k] (0..3) に色 COLORS[cols[k] + 1] の quad を置く item を n 個分
-- (n >= #cols、残りは描かない詰め物) 作る
local function items(cols, n)
	local t = {}
	for k = 1, n do
		local i = cols[k] or 0
		local col = COLORS[i + 1]
		local b = (k - 1) * 8
		t[b + 1], t[b + 2], t[b + 3], t[b + 4] = column_x(i), 0, 0.2, 0
		t[b + 5], t[b + 6], t[b + 7], t[b + 8] = col[1], col[2], col[3], 0
	end
	return t
end

local function draw(buf, instances)
	lub.gfx.draw(6, { item = buf }, {
		shader = shader,
		depth = false,
		cull = lub.gfx.NONE,
		instance_count = instances,
	})
end

local function target()
	return lub.gfx.use_texture("rw_rt", W, H, lub.gfx.RGBA8, nil, 1, { target = true })
end

-- 描いた列の色 (want[i + 1] が列 i)
local function colors_of(cols)
	local want = { BLACK, BLACK, BLACK, BLACK }
	for _, i in ipairs(cols) do
		want[i + 1] = COLORS[i + 1]
	end
	return want
end

-- frame ごとの大きさ (item の数)。描くのは先頭の min(n, 4) 個
local sizes = { 1, 4, 2, 3, 1, 40, 4, 2, 64, 1 }

local steps = {
	{
		name = "rewritten 3 times in one pass (same size)",
		run = function(rt)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			for i = 0, 2 do
				local buf = lub.gfx.use_buffer("rw_same", lub.gfx.STORAGE, items({ i }, 1))
				draw(buf, 1)
			end
			lub.gfx.end_pass()
			return colors_of({ 0, 1, 2 })
		end,
	},
	{
		name = "rewritten 3 times in one pass (size changes)",
		run = function(rt)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			local n = { 1, 3, 2 }
			for k = 1, 3 do
				local i = k
				local buf = lub.gfx.use_buffer("rw_size", lub.gfx.STORAGE, items({ i }, n[k]))
				draw(buf, 1)
			end
			lub.gfx.end_pass()
			return colors_of({ 1, 2, 3 })
		end,
	},
}

for _, n in ipairs(sizes) do
	steps[#steps + 1] = {
		name = "size " .. n .. " items across frames",
		run = function(rt)
			local cols = {}
			for i = 0, math.min(n, 4) - 1 do
				cols[#cols + 1] = i
			end
			local buf = lub.gfx.use_buffer("rw_grow", lub.gfx.STORAGE, items(cols, n))
			handle = handle or buf.handle
			expect(buf.handle == handle, "the handle must not change when the size changes")
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			draw(buf, #cols)
			lub.gfx.end_pass()
			return colors_of(cols)
		end,
	}
end

local function pixel(bytes, stride, x, y)
	local o = y * stride + x * 4
	return bytes[o + 1], bytes[o + 2], bytes[o + 3]
end

local function check_column(bytes, stride, i, col, what)
	local x = i * 8 + 4
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

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
	rb = lub.gfx.readback("rw_rb")
end

function M.on_frame()
	f = f + 1
	shader = lub.gfx.use_shader("rw_sh", VS, FS, 1)
	local rt = target()
	local request = nil
	if not requested then
		requested = { id = 100 + step, cols = steps[step].run(rt) }
		request = requested.id
	end
	local status, bytes, _, _, _, stride, id = rb:read_texture(rt, request)
	expect(status ~= "error", "readback failed")
	if status == "ready" and id == requested.id then
		for i = 0, 3 do
			check_column(bytes, stride, i, requested.cols[i + 1], steps[step].name)
		end
		step = step + 1
		requested = nil
		if step > #steps then
			print("BUFFER_REWRITE_OK steps=" .. #steps)
			lub.quit()
		end
	end
	expect(f < 300, "readback for step " .. step .. " did not arrive")
end

return M
