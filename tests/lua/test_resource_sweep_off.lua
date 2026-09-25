-- resource_sweep_after_frames = 0 は sweep しない。使われなくなった resource
-- (buffer / texture / snd / readback queue) もそのまま残る。
local M = {}

local f = 0
local rb

local function fail(message)
	print("RESOURCE_SWEEP_OFF_FAIL frame " .. f .. ": " .. message)
	os.exit(1, true)
end

function M.on_init()
	lub.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 64,
		height = 64,
		resource_sweep_after_frames = 0,
	})
	rb = lub.gfx.readback("so_rb")
end

function M.on_frame()
	f = f + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	if f == 1 then
		lub.gfx.use_buffer("so_idle", lub.gfx.STORAGE, { 0, 0, 0, 0 }, 1)
		local tex = lub.gfx.use_texture("so_tex", 2, 2, lub.gfx.RGBA8, nil, 1, { target = true })
		lub.gfx.begin_pass({ target = tex })
		lub.gfx.end_pass()
		rb:read_texture(tex, 1)
		lub.audio.snd("so_snd", { 0, 0.1, 0, -0.1 }, 1, 48000, 1)
		return
	end
	if f == 30 then
		if lub.gfx.lookup_buffer("so_idle") == nil or lub.gfx.lookup_texture("so_tex") == nil then
			fail("resources must not be swept when resource_sweep_after_frames = 0")
		end
		if lub.audio.snd("so_snd", nil, 1, 48000, 1) == nil then
			fail("snds must not be swept when resource_sweep_after_frames = 0")
		end
		-- 最初の frame に積んだ読み戻しがまだ queue にある
		local status, _, _, _, _, _, id = rb:read_texture(lub.gfx.lookup_texture("so_tex"))
		if status ~= "ready" or id ~= 1 then
			fail("readback queue must not be swept: status=" .. tostring(status) .. " id=" .. tostring(id))
		end
		print("RESOURCE_SWEEP_OFF_OK")
		os.exit(0, true)
	end
end

return M
