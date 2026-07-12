-- tests/lua/test_depth_sample.lua
-- depth-only pass (color target なし) で DEPTH32F に三角形を描き、
-- その depth texture を通常の texture としてサンプルして可視化する visual test。
-- 期待: 三角形領域が暗いグレー (z=0.25)、背景が白 (clear_depth=1.0)。
-- shadow mapping (depth-only pass + depth 直接サンプル) の成立条件そのもの。

local lub_io = require("lub_io")
local M = {}

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
	local wvs, ver1 = lub_io.load_text("tests/lua/test_depth_sample.vs.slang")
	local wfs, ver2 = lub_io.load_text("tests/lua/test_depth_sample.fs.slang")
	local rvs, ver3 = lub_io.load_text("tests/lua/test_depth_sample_read.vs.slang")
	local rfs, ver4 = lub_io.load_text("tests/lua/test_depth_sample_read.fs.slang")
	if not (wvs and wfs and rvs and rfs) then
		return
	end
	local write_sh = use_shader("sh_w", wvs, wfs, ver1 ~ ver2)
	local read_sh = use_shader("sh_r", rvs, rfs, ver3 ~ ver4)

	local dt = use_texture("dt", 256, 256, DEPTH32F, nil, 1, { target = true, filter = NEAREST, wrap = CLAMP })

	local tri = use_buffer("vb_tri", VERTEX, { -0.7, -0.7, 0.7, -0.7, 0.0, 0.7 }, 1)
	local quad = use_buffer("vb_quad", VERTEX, { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 }, 1)

	begin_pass({ depth_target = dt, clear_depth = 1.0 })
	draw(3, { verts = tri, uniforms = { z = { 0.25, 0, 0, 0 } } }, { shader = write_sh, depth = true, cull = NONE })
	end_pass()

	begin_pass({ target = main_tex, clear_color = { 1, 0, 1, 1 } })
	draw(6, { verts = quad, dtex = dt }, { shader = read_sh, depth = false, cull = NONE })
	end_pass()
end

return M
