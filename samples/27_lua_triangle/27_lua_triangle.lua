-- raw Lua のサンプル。C runtime が作る lub table (lub.gfx / lub.io / ...) だけを
-- 使う。entry は on_init / on_frame を持つ table を返す module で、
--   lub samples/27_lua_triangle/27_lua_triangle.lua
-- で動く (編集すると hot reload)。
local M = {}

function M.on_init()
	lub.config({})
end

function M.on_frame(dt)
	-- load_* は (data, version, status, error) を返す。data が nil なら未着 (web の
	-- fetch 中) か error なので、その frame は描かない。
	local vs, vsv = lub.io.load_text("samples/27_lua_triangle/data/27_lua_triangle.vs.slang")
	local fs, fsv = lub.io.load_text("samples/27_lua_triangle/data/27_lua_triangle.fs.slang")
	local verts, vv = lub.io.load_floats("samples/27_lua_triangle/data/27_lua_triangle.verts.lua")
	if vs == nil or fs == nil or verts == nil then
		return
	end
	-- version は内容の hash。同じ version なら data は読まれない (再宣言だけ)。
	local shader = lub.gfx.use_shader("tri27_shader", vs, fs, vsv * 31 + fsv)
	local vbuf = lub.gfx.use_buffer("tri27_verts", lub.gfx.VERTEX, verts, vv)
	if shader == nil or vbuf == nil then
		return
	end
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.1, 0.1, 0.2, 1.0 } })
	lub.gfx.draw(3, { verts = vbuf }, { shader = shader, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()
end

return M
