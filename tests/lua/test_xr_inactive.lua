-- tests/lua/test_xr_inactive.lua
-- XR セッションが無い backend での lub.xr の面。active / focused は false、
-- view / input は nil を返し、眼の指定が不正なら error になる。main_tex への
-- pass は通常どおり begin できる。

local M = {}

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "vulkan" })
end

local function verify()
	assert(lub.xr.active() == false)
	assert(lub.xr.focused() == false)
	assert(lub.xr.view(0, 0.05, 500) == nil)
	assert(lub.xr.view(1, 0.05, 500) == nil)
	assert(lub.xr.input(0) == nil)
	assert(lub.xr.input(1) == nil)
	assert(not pcall(lub.xr.view, 2, 0.05, 500))
	assert(not pcall(lub.xr.view, 0, 0, 500))
	assert(not pcall(lub.xr.view, 0, 500, 0.05))
	assert(not pcall(lub.xr.input, 2))
	lub.gfx.begin_pass({ target = lub.gfx.main_tex })
	lub.gfx.end_pass()
	print("XR_INACTIVE_OK")
end

function M.on_frame()
	local ok, err = pcall(verify)
	if not ok then
		io.stderr:write(err .. "\n")
		os.exit(1)
	end
	lub.app.quit()
end

return M
