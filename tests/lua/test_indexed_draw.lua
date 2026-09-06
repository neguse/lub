-- tests/lua/test_indexed_draw.lua
-- 4 頂点 quad を 6 index で indexed draw する最小テスト。
-- vertex 重複なしで quad を成立させられることが indexed draw の動作確認になる。

local M = {}

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
	local vs, ver_vs = lub.io.load_text("tests/lua/test_indexed_draw.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_indexed_draw.fs.slang")
	if not vs or not fs then
		return
	end
	local s = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)

	local verts = { -0.6, -0.6, 0.6, -0.6, 0.6, 0.6, -0.6, 0.6 }
	local vb = lub.gfx.use_buffer("vb", lub.gfx.VERTEX, verts, 1)
	local indices = { 0, 1, 2, 0, 2, 3 }
	local ib = lub.gfx.use_buffer("ib", lub.gfx.INDEX, indices, 1)

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.05, 0.05, 0.1, 1 } })
	lub.gfx.draw(6, { verts = vb, indices = ib }, { shader = s, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()
end

return M
