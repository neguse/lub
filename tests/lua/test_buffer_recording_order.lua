-- tests/lua/test_buffer_recording_order.lua
-- 1 フレームの中で同じ key の STORAGE buffer を違う内容で宣言し直し、それぞれ
-- の後に draw で読む。draw は宣言を記録した順の内容を読むので、赤で宣言した
-- 後の左半分は赤、緑で宣言し直した後の右半分は緑になる。書き込みが記録順を
-- 追い越す backend では両方とも最後の緑になる。

local M = {}

local rb = nil
local frames = 0
local requested = false

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("buffer_recording_order")
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

local function pixel(bytes, stride, x)
	local off = stride + x * 4
	return bytes:get(off), bytes:get(off + 1), bytes:get(off + 2)
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "test_buffer_recording_order: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_buffer_recording_order.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_buffer_recording_order.fs.slang")
	if not vs or not fs then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local rt = lub.gfx.use_texture("rt", 4, 4, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})
	local opts = { shader = sh, depth = false, cull = lub.gfx.NONE }

	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
	local red = lub.gfx.use_buffer("col", lub.gfx.STORAGE, { 1, 0, 0, 1 }, frames * 2)
	lub.gfx.draw(6, { col = red, uniforms = { side = { 0, 0, 0, 0 } } }, opts)
	local green = lub.gfx.use_buffer("col", lub.gfx.STORAGE, { 0, 1, 0, 1 }, frames * 2 + 1)
	lub.gfx.draw(6, { col = green, uniforms = { side = { 1, 0, 0, 0 } } }, opts)
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
	local lr, lg, lb = pixel(bytes, stride, 1)
	local rr, rg, rb_ = pixel(bytes, stride, 2)
	expect(
		lr == 255 and lg == 0 and lb == 0 and rr == 0 and rg == 255 and rb_ == 0,
		string.format(
			"test_buffer_recording_order: left (%d, %d, %d) want (255, 0, 0), right (%d, %d, %d) want (0, 255, 0)",
			lr,
			lg,
			lb,
			rr,
			rg,
			rb_
		)
	)
	print("OK test_buffer_recording_order")
	lub.app.quit()
end

return M
