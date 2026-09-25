-- lub.gfx.transient_buffer と frame の途中の submit (読み戻し):
--   * 読み戻しは (backend によっては) そこまでの frame の command を GPU に
--     出す。その前に作った transient を、読み戻しの後の pass で新しい
--     transient と並べてもう一度束縛しても、どの draw も自分の data を読む
--   * frame ごとに数百の transient (STORAGE と INDEX、大きさはいろいろ) を
--     作る。読み戻す frame と読み戻さない frame (GPU の仕事が次の frame に
--     残る) を交互にして、backend が frame をまたいで確保を使い回すところを
--     何周も通る
-- 結果は 2 つの offscreen target を読み戻して画素で確かめる。
local M = {}

local W, H = 32, 4
local FRAMES = 36
local LOAD = 400 -- frame ごとの負荷の transient (と draw) の数
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"
local f = 0
local rb
local shader
local expected = {} -- 読み戻しの id -> { name, cols }
local pending = 0
local checks = 0

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
local GRAY = { 0.25, 0.25, 0.25 }
local COLORS = { RED, GREEN, BLUE, WHITE }
local QUAD = { 0, 1, 2, 3, 4, 5 }

local function fail(message)
	print("TRANSIENT_MIDFRAME_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

-- 列 i (0..3) の中心の x (NDC) と、その列の中の画素の x
local function column_x(i)
	return -0.75 + i * 0.5
end
local function column_px(i)
	return i * 8 + 4
end

-- 列 i のこの frame の色 (frame ごとに巡るので、前の frame の data を読むと違う色)
local function color(i)
	return COLORS[(i + f) % 4 + 1]
end

-- 列 i に置く item。n 個の float (先頭 8 個のあとは詰め物) を使い回す table から
local scratch = {}
for i = 1, 8 + 4 * 32 do
	scratch[i] = 0
end
local function item(i, col, n)
	scratch[1], scratch[2], scratch[3], scratch[4] = column_x(i), 0, 0.2, 0
	scratch[5], scratch[6], scratch[7], scratch[8] = col[1], col[2], col[3], 0
	return lub.gfx.transient_buffer(lub.gfx.STORAGE, scratch, n)
end

local function draw(t, indices)
	lub.gfx.draw(6, { item = t, indices = indices }, { shader = shader, depth = false, cull = lub.gfx.NONE })
end

local function target(key)
	return lub.gfx.use_texture(key, W, H, lub.gfx.RGBA8, nil, 1, { target = true })
end

local function check_color(bytes, stride, x, col, what)
	for y = 0, H - 1 do
		local o = y * stride + x * 4
		local r, g, b = bytes[o + 1], bytes[o + 2], bytes[o + 3]
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

-- 届いた結果を確かめる (結果は要求の順に、後の read_texture で届く)
local function consume(status, bytes, _, _, _, stride, id, dropped, err)
	expect(status ~= "error", "readback failed: " .. tostring(err))
	expect(status ~= "dropped", "readback " .. tostring(dropped) .. " was dropped")
	if status ~= "ready" then
		return
	end
	local want = expected[id]
	expect(want ~= nil, "unexpected readback id " .. tostring(id))
	for i = 0, 3 do
		check_color(bytes, stride, column_px(i), want.cols[i + 1], want.name)
	end
	expected[id] = nil
	pending = pending - 1
	checks = checks + 1
end

local function request(rt, id, name, cols)
	expected[id] = { name = string.format("frame %d %s", f, name), cols = cols }
	pending = pending + 1
	consume(rb:read_texture(rt, id))
end

function M.on_init()
	lub.config({ backend = backend, width = 64, height = 64 })
	rb = lub.gfx.readback("tm_rb")
end

function M.on_frame()
	f = f + 1
	if f == 1 then
		shader = lub.gfx.use_shader("tm_draw", VS, FS, 1)
	end
	local rt_a, rt_b = target("tm_a"), target("tm_b")
	if f > FRAMES then
		consume(rb:read_texture(rt_a))
		if pending == 0 then
			-- 1 frame おきに 2 つずつ
			expect(checks == FRAMES, "want " .. FRAMES .. " checked readbacks, got " .. checks)
			print("TRANSIENT_MIDFRAME_OK checks=" .. checks)
			lub.quit()
		end
		expect(f < FRAMES + 60, pending .. " readbacks did not arrive")
		return
	end

	-- 負荷: LOAD 個の transient と draw (16 個に 1 つは INDEX の transient で
	-- indexed)。灰色で、確かめる列の下に隠れる。大きさ (float 8..56 個) は
	-- 13 通りが毎 frame 巡り、最後の 1 つ (60..88 個) は 8 frame の周期で変わる
	lub.gfx.begin_pass({ target = rt_a, clear_color = { 0, 0, 0, 1 } })
	for k = 1, LOAD do
		local n = 8 + ((f * 7 + k) % 13) * 4
		if k == LOAD then
			n = 8 + (13 + f % 8) * 4
		end
		local indices = nil
		if k % 16 == 0 then
			indices = lub.gfx.transient_buffer(lub.gfx.INDEX, QUAD)
		end
		draw(item(k % 4, GRAY, n), indices)
	end
	-- 確かめる列。大きさ (104..120 個) は負荷と重ならず、列ごとに違い、
	-- 5 frame の周期で変わる
	local a, size = {}, {}
	for i = 0, 3 do
		size[i] = 8 + (24 + (f + i) % 5) * 4
		a[i] = item(i, color(i), size[i])
		draw(a[i])
	end
	lub.gfx.end_pass()
	if f % 2 == 1 then
		consume(rb:read_texture(rt_a))
		return
	end

	-- 読み戻し (frame の途中の submit)。そのあと、前に作った a[0] と a[2] を、
	-- 同じ大きさの新しい transient と並べて別の target に描く (submit で確保を
	-- 配り直してしまう backend では、新しい方が a[0] と a[2] を上書きする)
	request(rt_a, f * 10, "rt_a (before the submit)", { color(0), color(1), color(2), color(3) })
	lub.gfx.begin_pass({ target = rt_b, clear_color = { 0, 0, 0, 1 } })
	draw(a[0])
	draw(item(1, color(3), size[0]))
	draw(a[2])
	draw(item(3, color(1), size[2]), lub.gfx.transient_buffer(lub.gfx.INDEX, QUAD))
	lub.gfx.end_pass()
	request(rt_b, f * 10 + 1, "rt_b (after the submit)", { color(0), color(3), color(2), color(1) })
end

return M
