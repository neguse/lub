-- tests/lua/test_depth_sample.lua
-- depth-only pass (color target なし) で lub.gfx.DEPTH32F に三角形を描き、
-- その depth texture を通常の texture としてサンプルして可視化する visual test。
-- 期待: 三角形領域が暗いグレー (z=0.25)、背景が白 (clear_depth=1.0)。
-- shadow mapping (depth-only pass + depth 直接サンプル) の成立条件そのもの。

local M = {}

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
	local wvs, ver1 = lub.io.load_text("tests/lua/test_depth_sample.vs.slang")
	local wfs, ver2 = lub.io.load_text("tests/lua/test_depth_sample.fs.slang")
	local rvs, ver3 = lub.io.load_text("tests/lua/test_depth_sample_read.vs.slang")
	local rfs, ver4 = lub.io.load_text("tests/lua/test_depth_sample_read.fs.slang")
	if not (wvs and wfs and rvs and rfs) then
		return
	end
	local write_sh = lub.gfx.use_shader("sh_w", wvs, wfs, ver1 ~ ver2)
	local read_sh = lub.gfx.use_shader("sh_r", rvs, rfs, ver3 ~ ver4)

	local dt = lub.gfx.use_texture(
		"dt",
		256,
		256,
		lub.gfx.DEPTH32F,
		nil,
		1,
		{ target = true, filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	)

	local tri = lub.gfx.use_buffer("vb_tri", lub.gfx.VERTEX, { -0.7, -0.7, 0.7, -0.7, 0.0, 0.7 }, 1)
	local quad = lub.gfx.use_buffer("vb_quad", lub.gfx.VERTEX, { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 }, 1)

	lub.gfx.begin_pass({ depth_target = dt, clear_depth = 1.0 })
	lub.gfx.draw(
		3,
		{ verts = tri, uniforms = { z = { 0.25, 0, 0, 0 } } },
		{ shader = write_sh, depth = true, cull = lub.gfx.NONE }
	)
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 1, 0, 1, 1 } })
	lub.gfx.draw(6, { verts = quad, dtex = dt }, { shader = read_sh, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()
end

return M
