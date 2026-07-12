-- tests/lua/test_load_op.lua
-- begin_pass の load = LOAD が color と depth の両方を保持することの visual test。
-- pass 1: 赤クリア + 左に緑 quad (z=0.4)。
-- pass 2: load = LOAD で中央に大きな青 quad (z=0.8, depth test あり)。
-- 期待: 背景の赤が残り (color load)、緑 quad が青 quad を遮る (depth load)。

local lub_io = require("lub_io")
local M = {}

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.onEvent(e) end
function M.onQuit() end

local function quad_verts(x0, y0, x1, y1)
	return { x0, y0, x1, y0, x1, y1, x0, y0, x1, y1, x0, y1 }
end

function M.onFrame()
	local vs, ver_vs = lub_io.load_text("tests/lua/test_load_op.vs.slang")
	local fs, ver_fs = lub_io.load_text("tests/lua/test_load_op.fs.slang")
	if not vs or not fs then
		return
	end
	local s = use_shader("sh", vs, fs, ver_vs ~ ver_fs)

	local green = use_buffer("vb_green", VERTEX, quad_verts(-0.8, -0.4, -0.2, 0.4), 1)
	local blue = use_buffer("vb_blue", VERTEX, quad_verts(-0.6, -0.6, 0.6, 0.6), 1)

	begin_pass({ target = main_tex, clear_color = { 1, 0, 0, 1 } })
	draw(6, { verts = green, uniforms = { zcol = { 0.4, 0.0, 1.0, 0.0 } } }, { shader = s, depth = true, cull = NONE })
	end_pass()

	begin_pass({ target = main_tex, load = LOAD })
	draw(6, { verts = blue, uniforms = { zcol = { 0.8, 0.0, 0.0, 1.0 } } }, { shader = s, depth = true, cull = NONE })
	end_pass()
end

return M
