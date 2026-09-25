-- resource_sweep_after_frames の既定 (300)。config で指定しなければ、使われ
-- なくなった resource は 300 frame の間残り、その次の frame の終わりに sweep
-- される。
local M = {}

local f = 0
local DEFAULT = 300

local function fail(message)
	print("RESOURCE_SWEEP_DEFAULT_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
end

function M.on_frame()
	f = f + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	if f == 1 then
		lub.gfx.use_buffer("sd_idle", lub.gfx.STORAGE, { 0, 0, 0, 0 }, 1)
		return
	end
	local alive = lub.gfx.lookup_buffer("sd_idle") ~= nil
	-- f frame 目は宣言から f - 1 frame 後。sweep は frame の終わりに走る
	if f <= DEFAULT + 2 then
		if not alive then
			fail("swept before " .. DEFAULT .. " unused frames")
		end
	elseif alive then
		fail("not swept after " .. DEFAULT .. " unused frames")
	else
		print("RESOURCE_SWEEP_DEFAULT_OK frame=" .. f)
		os.exit(0, true)
	end
end

return M
