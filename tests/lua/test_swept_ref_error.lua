-- tests/lua/test_swept_ref_error.lua
-- 宣言が途切れて sweep された resource の参照 (古い ref) を draw の bindings や
-- begin_pass の target に渡すと、黙って束縛が外れるのではなく error になる。
-- 宣言し直せば同じ ref で (key から引き直して) また使える。

local M = {}

local SWEEP = 2
local frame = 0
local buf, tex, shader
local vs, fs

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", resource_sweep_after_frames = SWEEP })
end

local function declare_shader()
	return lub.gfx.use_shader("swept_sh", vs, fs, 1)
end

function M.on_frame()
	frame = frame + 1
	if not vs then
		vs = lub.io.load_text("tests/lua/test_swept_ref_error.vs.slang")
		fs = lub.io.load_text("tests/lua/test_swept_ref_error.fs.slang")
		if not vs or not fs then
			return
		end
	end
	if frame == 1 then
		buf = lub.gfx.use_buffer("swept_buf", lub.gfx.STORAGE, { -1, -1, 3, -1, -1, 3 }, 1)
		tex = lub.gfx.use_texture("swept_tex", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
		return
	end
	-- frame 2..5: 宣言しない (shader だけ毎 frame 宣言して生かす)
	shader = declare_shader()
	if frame < 6 then
		return
	end
	if frame == 6 then
		lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
		local ok, err = pcall(lub.gfx.draw, 3, { verts = buf }, { shader = shader, cull = lub.gfx.NONE })
		lub.gfx.end_pass()
		assert(not ok, "draw with a swept buffer must fail")
		assert(string.find(err, "swept", 1, true), "error must say swept: " .. tostring(err))
		assert(string.find(err, "swept_buf", 1, true), "error must name the key: " .. tostring(err))
		assert(string.find(err, tostring(SWEEP), 1, true), "error must say how many frames: " .. tostring(err))

		local ok2, err2 = pcall(lub.gfx.begin_pass, { target = tex, clear_color = { 0, 0, 0, 1 } })
		if ok2 then
			lub.gfx.end_pass()
		end
		assert(not ok2, "begin_pass with a swept texture must fail")
		assert(string.find(err2, "swept", 1, true), "error must say swept: " .. tostring(err2))
		assert(string.find(err2, "swept_tex", 1, true), "error must name the key: " .. tostring(err2))

		-- 宣言し直すと古い ref も key から引き直せる
		lub.gfx.use_buffer("swept_buf", lub.gfx.STORAGE, { -1, -1, 3, -1, -1, 3 }, 1)
		lub.gfx.use_texture("swept_tex", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
		lub.gfx.begin_pass({ target = tex, clear_color = { 0, 0, 0, 1 } })
		lub.gfx.draw(3, { verts = buf }, { shader = shader, cull = lub.gfx.NONE })
		lub.gfx.end_pass()
		print("SWEPT_REF_ERROR_OK")
		lub.app.quit()
	end
end

return M
