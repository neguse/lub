-- tests/lua/test_gfx_in_on_init.lua
-- backend は on_init の後に起動する。on_init の中で backend を使う API
-- (use_buffer / begin_pass など) を呼ぶと落ちるのではなく error になる。
-- 同じ呼び出しは on_frame では通る。

local M = {}

local data = { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 }

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	local ok, err = pcall(lub.gfx.use_buffer, "init_test", lub.gfx.STORAGE, data)
	assert(not ok, "use_buffer in on_init must fail")
	assert(string.find(err, "on_init", 1, true), "error must mention on_init: " .. tostring(err))
	local ok2, err2 = pcall(lub.gfx.begin_pass, { target = lub.gfx.main_tex })
	assert(not ok2, "begin_pass in on_init must fail")
	assert(string.find(err2, "on_init", 1, true), "error must mention on_init: " .. tostring(err2))
	local ok3, err3 = pcall(lub.gfx.use_texture, "init_tex", 2, 2, lub.gfx.RGBA8, nil, 1, { target = true })
	assert(not ok3, "use_texture in on_init must fail")
	assert(string.find(err3, "on_init", 1, true), "error must mention on_init: " .. tostring(err3))
end

function M.on_frame()
	local b = lub.gfx.use_buffer("init_test", lub.gfx.STORAGE, data)
	assert(b ~= nil and b.handle ~= 0, "use_buffer in on_frame must succeed")
	local t = lub.gfx.use_texture("init_tex", 2, 2, lub.gfx.RGBA8, nil, 1, { target = true })
	assert(t ~= nil and t.handle ~= 0, "use_texture in on_frame must succeed")
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	print("GFX_IN_ON_INIT_OK")
	lub.app.quit()
end

return M
