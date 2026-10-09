-- lubx.Mesh3d と MeshText が GPU buffer を毎 frame 再主張することを見る。
-- resource_sweep_after_frames を小さくし、同じ mesh と glyph を sweep 期間より
-- 長く描き続けても stale handle の error にならないことを確かめる。
package.path = "samples/?.lua;" .. package.path
local lubx = require("lubx")

local M = {}

local SWEEP_FRAMES = 2
local FRAMES = 8
local FONT_PATH = "samples/data/fonts/MPLUS1p-subset.ttf"

local function fail(message)
	print("MESH_REASSERT_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local frame = 0
local ren = lubx.Renderer3d.new("mesh_reassert")
local cube = lubx.Mesh3d.new("mesh_reassert_cube")
local text = lubx.MeshText.new("mesh_reassert_text", FONT_PATH, 1, 320, 180)
local cam = lubx.Camera.new()
cam.eye = lubx.Vec3.new(0, 1.5, -4)
cam.target = lubx.Vec3.new(0, 0, 0)

function M.on_init()
	lub.app.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 320,
		height = 180,
		resource_sweep_after_frames = SWEEP_FRAMES,
	})
end

local function draw()
	ren:begin(cam)
	ren:draw(cube, lubx.Mat4.identity())
	ren:end_()
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, load = lub.gfx.LOAD })
	text:text("Ab", 10, 100, 48)
	lub.gfx.end_pass()
end

function M.on_frame()
	frame = frame + 1
	if frame == 1 then
		-- gfx は on_init では使えないので最初の frame で upload する
		cube:rebuild(lubx.Shapes3d.cube())
		expect(cube:ready(), "cube must be ready after rebuild")
	else
		-- 再主張されていなければ sweep 後の draw は stale handle になる。
		-- draw の前に lookup で見て、落ちる前に message を出す。
		expect(lub.gfx.lookup_buffer("mesh_reassert_cube_vb") ~= nil, "frame " .. frame .. ": cube vb was swept")
		expect(lub.gfx.lookup_buffer("mesh_reassert_text_v:65") ~= nil, "frame " .. frame .. ": glyph vb was swept")
	end
	local ok, err = pcall(draw)
	expect(ok, "frame " .. frame .. ": " .. tostring(err))
	if frame == FRAMES then
		print("MESH_REASSERT_OK")
		lub.app.quit()
	end
end

return M
