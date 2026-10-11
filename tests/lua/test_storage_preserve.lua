-- tests/lua/test_storage_preserve.lua
-- compute が読み書きする storage buffer と storage texture は、前の dispatch
-- が書いた内容を保つ (neguse/lub#184)。3 フレーム続けて 1 フレームに 2 回
-- dispatch し、各 dispatch は acc[0] を読んで 1 足して書き、img の自分の列
-- (dispatch の番号) に 1 を書く。最後のフレームで acc と img を rt に描いて
-- 読み戻す。期待: acc は 6、img は 6 列とも 1。前の内容を捨てて別の領域に
-- 差し替える backend では acc が 6 に届かず、前の dispatch の列が消える。

local M = {}

local FRAMES = 3
local PER_FRAME = 2
local N = FRAMES * PER_FRAME
local rb = nil
local frames = 0

function M.OnInit()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("storage_preserve")
end

function M.OnEvent(e) end
function M.OnQuit() end

local function fail(message)
	print("FAIL " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.OnFrame()
	frames = frames + 1
	expect(frames < 300, "test_storage_preserve: readback never became ready")

	local cs, ver_cs = lub.io.load_text("tests/lua/test_storage_preserve.cs.slang")
	local vs, ver_vs = lub.io.load_text("tests/lua/test_storage_preserve.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_storage_preserve.fs.slang")
	expect(cs and vs and fs, "test_storage_preserve: shader source missing")
	local sh_c = lub.gfx.use_shader_compute("cs", cs, ver_cs)
	local sh_r = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local acc = lub.gfx.use_buffer("acc", lub.gfx.STORAGE, { 0, 0, 0, 0 }, 1)
	local img = lub.gfx.use_texture("img", N, 1, lub.gfx.RGBA32F, nil, 1, {
		target = true,
		storage = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})
	local rt = lub.gfx.use_texture("rt", N, 1, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})

	if frames == 1 then
		lub.gfx.begin_pass({ target = img, clear_color = { 0, 0, 0, 0 } })
		lub.gfx.end_pass()
	end
	if frames <= FRAMES then
		for k = 0, PER_FRAME - 1 do
			local texel = (frames - 1) * PER_FRAME + k
			lub.gfx.dispatch(1, 1, 1, {
				acc = acc,
				img = img,
				uniforms = { texel = { texel, 0, 0, 0 } },
			}, { shader = sh_c })
		end
	end
	if frames == FRAMES then
		lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
		lub.gfx.draw(3, { img = img, acc = acc }, { shader = sh_r, depth = false, cull = lub.gfx.NONE })
		lub.gfx.end_pass()
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	if frames < FRAMES then
		return
	end
	local st, bytes, w, h, _, _, id, _, err
	if frames == FRAMES then
		st, bytes, w, h, _, _, id, _, err = rb:read_texture(rt, 1)
	else
		st, bytes, w, h, _, _, id, _, err = rb:read_texture(rt)
	end
	expect(st ~= "error", "test_storage_preserve: read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		return
	end
	expect(id == 1 and w == N and h == 1, "test_storage_preserve: unexpected readback result")
	local bad = {}
	for x = 0, N - 1 do
		local r, g = bytes:get(x * 4), bytes:get(x * 4 + 1)
		if r ~= 255 or g ~= N then
			bad[#bad + 1] = string.format("px %d: img %d want 255, acc %d want %d", x, r, g, N)
		end
	end
	expect(#bad == 0, "test_storage_preserve: " .. table.concat(bad, "; "))
	print("OK test_storage_preserve")
	lub.app.quit()
end

return M
