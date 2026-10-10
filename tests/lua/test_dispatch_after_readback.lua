-- tests/lua/test_dispatch_after_readback.lua
-- 同じフレームで read_texture の後に dispatch する (neguse/lub#66)。
-- 読み戻しの後の dispatch も、そのフレームの描画と同じ順で GPU に届く。
-- 毎フレーム: rt_a を描く → rt_a を読み戻す → dst = src*2+1 を dispatch →
-- dst を読む fragment で rt_b を描く → rt_b を読み戻す。届いた rt_b の
-- 読み戻し (早くても次のフレーム) の 4 列の画素を確かめる。

local M = {}

local FRAMES = 8
local SRC = { 0.05, 0.1, 0.15, 0.2 }
local rb_a, rb_b
local frame = 0
local verified = 0
local last_id = 0

local function fail(message)
	print("DISPATCH_AFTER_READBACK_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function expect_px(bytes, x)
	local v = SRC[x + 1] * 2.0 + 1.0 - 1.0
	local want = math.floor(v * 255.0 + 0.5)
	local r = bytes:get(x * 4)
	local g = bytes:get(x * 4 + 1)
	local b = bytes:get(x * 4 + 2)
	local a = bytes:get(x * 4 + 3)
	expect(
		math.abs(r - want) <= 2 and math.abs(g - want) <= 2 and math.abs(b - want) <= 2 and a == 255,
		string.format("frame %d px %d: got (%d,%d,%d,%d), want (%d,%d,%d,255)", frame, x, r, g, b, a, want, want, want)
	)
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb_a = lub.gfx.readback("a")
	rb_b = lub.gfx.readback("b")
end

function M.on_frame()
	if verified >= FRAMES then
		lub.app.quit()
		return
	end
	frame = frame + 1
	expect(frame < 300, string.format("verified %d of %d frames", verified, FRAMES))

	local cs, ver_cs = lub.io.load_text("tests/lua/test_dispatch_after_readback.cs.slang")
	local vs, ver_vs = lub.io.load_text("tests/lua/test_dispatch_after_readback.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_dispatch_after_readback.fs.slang")
	expect(cs and vs and fs, "shader source missing")
	local sh_c = lub.gfx.use_shader_compute("cs", cs, ver_cs)
	local sh_r = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local opts = { target = true, filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	local rt_a = lub.gfx.use_texture("rt_a", 4, 1, lub.gfx.RGBA8, nil, 1, opts)
	local rt_b = lub.gfx.use_texture("rt_b", 4, 1, lub.gfx.RGBA8, nil, 1, opts)
	local src = lub.gfx.use_buffer("src", lub.gfx.STORAGE, SRC, 1)
	-- dst は毎フレーム作り直し (version = frame) て、前フレームの結果が残らないようにする
	local dst = lub.gfx.use_buffer_empty("dst", lub.gfx.STORAGE, 4, frame)
	local quad = lub.gfx.use_buffer("quad", lub.gfx.STORAGE, { -1, -1, 1, -1, 1, 1, -1, 1 }, 1)

	-- 1. rt_a を描く
	lub.gfx.begin_pass({ target = rt_a, clear_color = { 1.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	-- 2. 読み戻しを積む (sdlgpu はここでフレームの command buffer を submit する)
	rb_a:read_texture(rt_a, frame)
	-- 3. 読み戻しの後に dispatch
	lub.gfx.dispatch(1, 1, 1, { src = src, dst = dst }, { shader = sh_c })
	-- 4. dst を読んで rt_b を描く
	lub.gfx.begin_pass({ target = rt_b, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.draw(6, { verts = quad, dst = dst }, { shader = sh_r, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()

	-- rt_b の読み戻しを毎フレーム積み、届いたものを要求順に確かめる
	local st, bytes, w, h, fmt, stride, id, _, err = rb_b:read_texture(rt_b, frame)
	expect(st ~= "error", "read_texture(rt_b) failed: " .. tostring(err))
	if st == "ready" then
		expect(id > last_id, string.format("read_texture(rt_b) id %s after %d", tostring(id), last_id))
		last_id = id
		expect(w == 4 and h == 1 and fmt == lub.gfx.RGBA8 and stride == 16, "unexpected readback shape")
		for x = 0, 3 do
			expect_px(bytes, x)
		end
		verified = verified + 1
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
end

return M
