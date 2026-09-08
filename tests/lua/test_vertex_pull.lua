-- tests/lua/test_vertex_pull.lua
-- 4 頂点を StructuredBuffer から SV_VertexID で引く (vertex pulling)。
-- 頂点 buffer と入力レイアウトを使わずに test_indexed_draw と同じ絵になる
-- ことが確認になる。

local M = {}

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
	local vs, ver_vs = lub.io.load_text("tests/lua/test_vertex_pull.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_indexed_draw.fs.slang")
	if not vs or not fs then
		return
	end
	local s = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)

	local verts = { -0.6, -0.6, 0.6, -0.6, 0.6, 0.6, -0.6, 0.6 }
	local sb = lub.gfx.use_buffer("sb", lub.gfx.STORAGE, verts, 1)

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.05, 0.05, 0.1, 1 } })
	lub.gfx.draw(6, { verts = sb }, { shader = s, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()
end

return M
