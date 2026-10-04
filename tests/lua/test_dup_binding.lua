-- tests/lua/test_dup_binding.lua
-- VS と FS の両方が同じ名前の StructuredBuffer (item) と texture (tex) を
-- 読む draw。どちらの stage にも resource が束縛されれば中央の pixel は
-- (255, 128, 255) になる。片方の stage だけに束縛される backend では
-- 描画そのものが落ちるか色が欠ける。
-- 期待: R = item[0] (VS)、B = item[1] (FS)、G = tex の半分 x 2 (VS + FS)。

local M = {}

local rb = nil
local frames = 0
local requested = false

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("dup_binding")
end

function M.on_event(e) end
function M.on_quit() end

local function fail(message)
	print("FAIL " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function near(v, want, tol)
	return math.abs(v - want) <= tol
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "test_dup_binding: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_dup_binding.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_dup_binding.fs.slang")
	if not vs or not fs then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local item = lub.gfx.use_buffer("item", lub.gfx.STORAGE, { 1, 0, 0, 0, 0, 0, 1, 0 }, 1)
	local tex = lub.gfx.use_texture("tex", 1, 1, lub.gfx.RGBA8, { 0, 128, 0, 255 }, 1, {
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})
	local rt = lub.gfx.use_texture("rt", 4, 4, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})

	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.draw(6, { item = item, tex = tex }, { shader = sh, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	local st, bytes, w, h, _, stride, id, _, err
	if not requested then
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt, 1)
		requested = true
	else
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt)
	end
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		return
	end
	expect(id == 1 and w == 4 and h == 4, "unexpected readback result")
	local off = 2 * stride + 2 * 4
	local r, g, b = bytes:get(off), bytes:get(off + 1), bytes:get(off + 2)
	expect(
		near(r, 255, 3) and near(g, 128, 3) and near(b, 255, 3),
		string.format("test_dup_binding: pixel (%d, %d, %d), want (255, 128, 255)", r, g, b)
	)
	print("OK test_dup_binding")
	lub.quit()
end

return M
