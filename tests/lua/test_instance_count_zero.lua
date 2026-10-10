-- tests/lua/test_instance_count_zero.lua
-- draw の instance_count に 0 以下を渡すと draw 自体が skip される
-- (stub の記述どおり)。1 なら描く。render target に描いて読み戻して確かめる。
-- 1 フレームに 1 通りずつ描いて読み戻しを積み、結果が届くまで poll する。

local M = {}

local CASES = {
	{ instance_count = 0, want = { 255, 0, 0, 255 }, what = "instance_count = 0 must not draw" },
	{ instance_count = -3, want = { 255, 0, 0, 255 }, what = "instance_count < 0 must not draw" },
	{ instance_count = 1, want = { 0, 255, 0, 255 }, what = "instance_count = 1 must draw" },
}

local rb = nil
local stage = 1
local requested = false
local frames = 0

local function fail(message)
	print("INSTANCE_COUNT_ZERO_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("test")
end

local function pixel(bytes, stride, x, y)
	local o = y * stride + x * 4
	return bytes[o + 1], bytes[o + 2], bytes[o + 3], bytes[o + 4]
end

function M.on_frame()
	if stage > #CASES then
		lub.app.quit()
		return
	end
	frames = frames + 1
	expect(frames < 300, "readback " .. stage .. " never became ready")
	local vs, ver_vs = lub.io.load_text("tests/lua/test_instance_count_zero.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_instance_count_zero.fs.slang")
	expect(vs and fs, "shader source missing")
	local s = lub.gfx.use_shader("ic_sh", vs, fs, ver_vs ~ ver_fs)
	-- 画面全体を覆う三角形
	local verts = lub.gfx.use_buffer("ic_vb", lub.gfx.STORAGE, { -1, -1, 3, -1, -1, 3 }, 1)

	local c = CASES[stage]
	local tex = lub.gfx.use_texture("ic_rt", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	lub.gfx.begin_pass({ target = tex, clear_color = { 1.0, 0.0, 0.0, 1.0 } })
	lub.gfx.draw(3, { verts = verts }, { shader = s, cull = lub.gfx.NONE, instance_count = c.instance_count })
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	local st, bytes, w, h, fmt, stride, rid, _, err = rb:read_texture(tex, not requested and stage or nil)
	requested = true
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		return
	end
	expect(rid == stage, string.format("readback id %s, want %d", tostring(rid), stage))
	expect(w == 4 and h == 4 and fmt == lub.gfx.RGBA8, "unexpected readback shape")
	local r, g, b, a = pixel(bytes, stride, 1, 1)
	expect(
		r == c.want[1] and g == c.want[2] and b == c.want[3] and a == c.want[4],
		string.format("%s (got %d,%d,%d,%d)", c.what, r, g, b, a)
	)
	stage = stage + 1
	requested = false
	if stage > #CASES then
		print("INSTANCE_COUNT_ZERO_OK")
	end
end

return M
