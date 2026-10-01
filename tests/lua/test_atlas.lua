-- lubx.Atlas の再主張が pixels を読まないことと、sweep 後に作り直すことを見る。
-- pixels を配列でない値に差し替え、渡せば use_texture が error になることで検出する。
package.path = "samples/?.lua;" .. package.path
local lubx = require("lubx")

local M = {}

local SWEEP_FRAMES = 2

local function fail(message)
	print("ATLAS_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function pixels()
	local px = {}
	for i = 1, 2 * 2 * 4 do
		px[i] = 255
	end
	return px
end

local frame = 0
local dynamic = lubx.Atlas.from_pixels("atlas_dynamic", 2, 2, pixels())
local fixed = lubx.Atlas.from_pixels("atlas_fixed", 2, 2, pixels(), 7)
local first_version

function M.on_init()
	lub.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 320,
		height = 180,
		resource_sweep_after_frames = SWEEP_FRAMES,
	})
end

function M.on_frame()
	frame = frame + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	if frame == 1 then
		expect(dynamic:ensure() and fixed:ensure(), "first ensure must upload")
		first_version = dynamic.texture.version
		dynamic.pixels = "poison"
		fixed.pixels = "poison"
	elseif frame == 2 then
		local ok, err = pcall(dynamic.ensure, dynamic)
		expect(ok, "reassertion must not read pixels: " .. tostring(err))
		expect(dynamic.texture.version == first_version, "reassertion must keep the version")
		ok, err = pcall(fixed.ensure, fixed)
		expect(ok, "constant version must not read pixels: " .. tostring(err))
	elseif frame == 3 + SWEEP_FRAMES + 1 then
		expect(lub.gfx.lookup_texture("atlas_dynamic") == nil, "unused atlas must be swept")
		expect(not pcall(dynamic.ensure, dynamic), "swept atlas must upload pixels again")
		dynamic.pixels = pixels()
		expect(dynamic:ensure(), "swept atlas must be recreated")
		expect(lub.gfx.lookup_texture("atlas_dynamic") ~= nil, "recreated atlas must be live")
		dynamic:update_pixels(pixels())
		local before = dynamic.texture.version
		dynamic:ensure()
		expect(dynamic.texture.version ~= before, "update_pixels must issue a new version")
		print("ATLAS_OK")
		lub.quit()
	end
end

return M
