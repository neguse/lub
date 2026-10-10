-- tests/lua/test_compute_uniform_block.lua
-- uniform block を持つ compute shader を、uniform 無し (2 group、目印を
-- mark に書く) と uniform 有り (1 group、値を val に書く) で dispatch し、
-- 両方の storage buffer を draw で可視化して読み戻す。uniform 無しの
-- dispatch が落ちる backend では目印 (R) が残らない。
-- 期待: 中央の pixel が (128, 128, 255)。

local M = {}

local rb = nil
local frames = 0
local requested = false

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("compute_uniform_block")
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
	expect(frames < 300, "test_compute_uniform_block: readback never became ready")

	local cs, ver_cs = lub.io.load_text("tests/lua/test_compute_uniform_block.cs.slang")
	local vs, ver_vs = lub.io.load_text("tests/lua/test_compute_uniform_block.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_compute_uniform_block.fs.slang")
	if not (cs and vs and fs) then
		return
	end
	local sh_c = lub.gfx.use_shader_compute("cs", cs, ver_cs)
	local sh_r = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local mark = lub.gfx.use_buffer("mark", lub.gfx.STORAGE, { 0, 0, 0, 0, 0, 0, 0, 0 }, 1)
	local val = lub.gfx.use_buffer("val", lub.gfx.STORAGE, { 0, 0, 0, 0, 0, 0, 0, 0 }, 1)
	local rt = lub.gfx.use_texture("rt", 4, 4, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})

	-- uniform 無し: tid 1 が目印を書く (tid 0 が書く値は読まない)。
	lub.gfx.dispatch(2, 1, 1, { out_buf = mark }, { shader = sh_c })
	-- uniform 有り: tid 0 が value を書く。
	lub.gfx.dispatch(1, 1, 1, { out_buf = val, uniforms = { value = { 0, 0.5, 1, 0 } } }, { shader = sh_c })

	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.draw(6, { mark = mark, val = val }, { shader = sh_r, depth = false, cull = lub.gfx.NONE })
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
		near(r, 128, 3) and near(g, 128, 3) and near(b, 255, 3),
		string.format("test_compute_uniform_block: pixel (%d, %d, %d), want (128, 128, 255)", r, g, b)
	)
	print("OK test_compute_uniform_block")
	lub.app.quit()
end

return M
