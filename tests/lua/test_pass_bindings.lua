-- begin_pass の bindings (PassOpts.Bindings):
--   * pass の uniforms はその pass のどの draw にも届く。uniform を 1 つも渡さない
--     draw (bindings が空) にも届く
--   * draw の bindings が同じ名前を持てば draw が勝つ (uniforms は member ごと、
--     buffer / texture は束縛の名前ごと)
--   * 値は begin_pass の時点で写す (渡した table を後で書き換えても効かない)
--   * pass の buffer (transient も) / texture は draw のたびに引く (pass の中で
--     宣言し直した texture も見える)
--   * end_pass で消える (次の pass の draw は渡さなかった member が 0)
--   * 検査は draw と同じ (前の frame の transient は begin_pass で error)
--   * 同じ pipeline の draw が続くと runtime は pipeline を backend に渡し直さ
--     ないが、ui.render (ImGui は自分の pipeline で描く) の後の draw は渡し直す
-- 結果は offscreen の target を読み戻して画素で確かめる。
local M = {}

local W, H = 32, 4
local f = 0
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"
local rb
local shaders = {}
local stale = nil
local step = 1
local requested = nil
local done_checks = {}

-- item[0] = (中心の x, _, 半幅, _)。高さは target 全体
local VS = [[
StructuredBuffer<float4> item;
static const float2 corner[6] = {
  float2(-1, -1), float2(1, -1), float2(1, 1),
  float2(-1, -1), float2(1, 1), float2(-1, 1),
};
struct VSOut {
  float2 uv : TEXCOORD0;
  float4 pos : SV_Position;
};
[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  float4 r = item[0];
  float2 c = corner[vid];
  VSOut o;
  o.uv = c * 0.5 + 0.5;
  o.pos = float4(r.x + c.x * r.z, c.y, 0.0, 1.0);
  return o;
}
]]

-- 色は color + add (FS の uniform block)
local FS_COLOR = [[
struct F {
  float4 color;
  float4 add;
};
ConstantBuffer<F> f;
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4((f.color + f.add).rgb, 1.0);
}
]]

-- 色は tex の中央
local FS_TEX = [[
LUB_TEXTURE2D(tex);
struct FSIn {
  float2 uv : TEXCOORD0;
};
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return float4(LUB_SAMPLE(tex, float2(0.5, 0.5)).rgb, 1.0);
}
]]

-- 列 i (0..3) の中心の x (NDC) と、その列の中の画素の x
local function column_x(i)
	return -0.75 + i * 0.5
end
local function column_px(i)
	return i * 8 + 4
end

local RED = { 1, 0, 0, 1 }
local GREEN = { 0, 1, 0, 1 }
local BLUE = { 0, 0, 1, 1 }
local MAGENTA = { 1, 0, 1, 1 }
local BLACK = { 0, 0, 0, 1 }
local ZERO = { 0, 0, 0, 0 }

local function fail(message)
	print("PASS_BINDINGS_FAIL frame " .. f .. ": " .. message)
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

-- 列 i に置く quad の item (この frame の transient)
local function item(i)
	return lub.gfx.transient_buffer(lub.gfx.STORAGE, { column_x(i), 0, 0.2, 0 })
end

local function opts(sh)
	return { shader = sh, depth = false, cull = lub.gfx.NONE }
end

-- 1 色の 2x2 texture (key と version で宣言)
local function solid(key, col, version, size)
	local n = size or 1
	local px = {}
	for i = 0, n * n - 1 do
		px[i * 4 + 1] = col[1] * 255
		px[i * 4 + 2] = col[2] * 255
		px[i * 4 + 3] = col[3] * 255
		px[i * 4 + 4] = 255
	end
	return lub.gfx.use_texture(key, n, n, lub.gfx.RGBA8, px, version, { filter = lub.gfx.NEAREST })
end

local function target(w, h)
	w, h = w or W, h or H
	return lub.gfx.use_texture("pb_rt" .. w .. "x" .. h, w, h, lub.gfx.RGBA8, nil, 1, { target = true })
end

-- 各 step は target に描き、読み戻した画素で確かめる列の色を返す
local steps = {
	{
		name = "pass uniforms and a pass buffer reach every draw; the draw's names win",
		run = function(rt)
			local pass_u = { color = { 1, 0, 0, 1 }, add = { 0, 0, 0, 0 } }
			lub.gfx.begin_pass({
				target = rt,
				clear_color = { 0, 0, 0, 1 },
				bindings = { item = item(3), uniforms = pass_u },
			})
			-- 写したあとの書き換えは pass に効かない
			pass_u.color[1], pass_u.color[2] = 0, 1
			pass_u.add = { 1, 1, 1, 0 }
			-- bindings が空の draw: item も uniforms も pass のもの
			lub.gfx.draw(6, {}, opts(shaders.color))
			-- item は draw のもの、uniforms は pass のもの
			lub.gfx.draw(6, { item = item(0) }, opts(shaders.color))
			-- color だけ draw のもの (add は pass のまま)
			lub.gfx.draw(6, { item = item(1), uniforms = { color = GREEN } }, opts(shaders.color))
			-- add だけ draw のもの (color は pass のまま)
			lub.gfx.draw(6, { item = item(2), uniforms = { add = BLUE } }, opts(shaders.color))
			lub.gfx.end_pass()
			return { RED, GREEN, MAGENTA, RED }
		end,
	},
	{
		name = "a pass texture reaches draws, is looked up per draw, and a draw's texture wins",
		run = function(rt)
			local red = solid("pb_tex", RED, 1)
			local blue = solid("pb_blue", BLUE, 1)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 }, bindings = { tex = red } })
			lub.gfx.draw(6, { item = item(0) }, opts(shaders.tex))
			lub.gfx.draw(6, { item = item(1), tex = blue }, opts(shaders.tex))
			-- 同じ key を別の大きさで宣言し直す (backend の texture は作り直し、
			-- handle は同じ)。pass の bindings は draw のたびに引くので新しい中身
			local again = solid("pb_tex", GREEN, 2, 2)
			expect(again.handle == red.handle, "a redeclared key must keep its handle")
			lub.gfx.draw(6, { item = item(2) }, opts(shaders.tex))
			lub.gfx.end_pass()
			return { RED, BLUE, GREEN, BLACK }
		end,
	},
	{
		name = "end_pass clears the pass bindings",
		run = function(rt)
			lub.gfx.begin_pass({
				target = rt,
				clear_color = { 0, 0, 0, 1 },
				bindings = { uniforms = { color = RED } },
			})
			lub.gfx.draw(6, { item = item(0) }, opts(shaders.color))
			lub.gfx.end_pass()
			-- 同じ target に描き足す pass。bindings は無いので color は 0
			lub.gfx.begin_pass({ target = rt, load = lub.gfx.LOAD })
			lub.gfx.draw(6, { item = item(1), uniforms = { add = BLUE } }, opts(shaders.color))
			lub.gfx.end_pass()
			return { RED, BLUE, BLACK, BLACK }
		end,
	},
	{
		name = "a draw after ui.render applies its pipeline again",
		-- ImGui は画面の大きさで描くので、target も画面と同じ大きさ
		size = 64,
		run = function(rt)
			lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
			lub.gfx.draw(6, { item = item(0), uniforms = { color = GREEN } }, opts(shaders.color))
			-- 右下の隅に window を 1 つ描く (列 0 / 1 の上半分には掛からない)
			lub.ui.set_next_window(40, 40, 20, 20)
			lub.ui.begin_window("pb_ui")
			lub.ui.text("ui")
			lub.ui.end_window()
			lub.ui.render()
			-- 直前の draw と同じ pipeline
			lub.gfx.draw(6, { item = item(1), uniforms = { color = BLUE } }, opts(shaders.color))
			lub.gfx.end_pass()
			-- 64 px の target の列 0 / 1 の中心と、上から 32 行
			return nil, { { x = 8, rows = 32, col = GREEN }, { x = 24, rows = 32, col = BLUE } }
		end,
	},
}

local function pixel(bytes, stride, x, y)
	local o = y * stride + x * 4
	return bytes[o + 1], bytes[o + 2], bytes[o + 3]
end

local function check_color(bytes, stride, x, rows, col, what)
	for y = 0, rows - 1 do
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
	local s = steps[step]
	local rt = target(s.size, s.size)
	local request = nil
	if not requested then
		local cols, points = s.run(rt)
		requested = { id = 100 + step, cols = cols, points = points or {} }
		request = requested.id
	end
	local status, bytes, _, _, _, stride, id = rb:read_texture(rt, request)
	expect(status ~= "error", "readback failed")
	if status == "ready" and id == requested.id then
		if requested.cols then
			for i = 0, 3 do
				check_color(bytes, stride, column_px(i), H, requested.cols[i + 1], s.name)
			end
		end
		for _, p in ipairs(requested.points) do
			check_color(bytes, stride, p.x, p.rows, p.col, s.name)
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
	rb = lub.gfx.readback("pb_rb")
end

function M.on_frame()
	f = f + 1
	shaders.color = lub.gfx.use_shader("pb_color", VS, FS_COLOR, 1)
	shaders.tex = lub.gfx.use_shader("pb_tex_sh", VS, FS_TEX, 1)
	if f == 1 then
		-- 検査は draw の bindings と同じ
		expect_error(
			"a number as a pass binding",
			"buffer or texture expected",
			lub.gfx.begin_pass,
			{ target = target(), bindings = { item = 3 } }
		)
		expect_error(
			"a string as a pass uniform",
			"number or number array expected",
			lub.gfx.begin_pass,
			{ target = target(), bindings = { uniforms = { color = "red" } } }
		)
		-- 次の frame で使う (前の frame の transient は error になる)
		stale = item(0)
		return
	end
	if f == 2 then
		expect_error(
			"a transient from the previous frame as a pass binding",
			"earlier frame",
			lub.gfx.begin_pass,
			{ target = target(), bindings = { item = stale } }
		)
		-- 失敗した begin_pass は pass を始めない
		lub.gfx.begin_pass({ target = target() })
		lub.gfx.end_pass()
		return
	end
	if run_steps() then
		print("PASS_BINDINGS_OK checks=" .. #done_checks)
		lub.quit()
	end
end

return M
