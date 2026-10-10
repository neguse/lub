-- tests/lua/test_readback_on_quit.lua
-- フレームの外 (on_quit) で積んだ read_texture も、次の poll で結果が届く。
-- 後にフレームが来ないので、backend はその場で読み戻しを終える。

local M = {}

local rb
local rt
local frames = 0

local function fail(message)
	print("READBACK_ON_QUIT_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("on_quit")
end

function M.on_frame()
	frames = frames + 1
	rt = lub.gfx.use_texture("rt", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true, filter = lub.gfx.NEAREST })
	lub.gfx.begin_pass({ target = rt, clear_color = { 0.0, 1.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	if frames == 2 then
		lub.app.quit()
	end
end

function M.on_quit()
	local st, bytes, _, _, _, _, id, _, err = rb:read_texture(rt, 1)
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		st, bytes, _, _, _, _, id, _, err = rb:read_texture(rt)
	end
	expect(st == "ready", "read_texture in on_quit was not ready: " .. tostring(st) .. " " .. tostring(err))
	expect(id == 1, "unexpected readback id " .. tostring(id))
	local px = { bytes:get(0), bytes:get(1), bytes:get(2), bytes:get(3) }
	expect(
		px[1] == 0 and px[2] == 255 and px[3] == 0 and px[4] == 255,
		string.format("pixel (%d,%d,%d,%d), want (0,255,0,255)", px[1], px[2], px[3], px[4])
	)
	print("OK test_readback_on_quit")
end

return M
