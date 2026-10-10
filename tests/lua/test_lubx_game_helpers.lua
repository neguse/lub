-- lubx のゲーム向け定型 (Assets.Wav / Assets.RenderTarget / Camera3d.Project /
-- Camera3d.ScreenRay / Ray / Rand.Between / Sfx.Synth) の振る舞いを見る。
-- 純粋な計算は on_init で、resource を宣言する定型は frame を回して確かめる。
package.path = "samples/?.lua;" .. package.path
local lubx = require("lubx")

local M = {}

local SWEEP_FRAMES = 2

local function fail(message)
	print("LUBX_GAME_HELPERS_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function near(a, b, eps, message)
	expect(math.abs(a - b) <= eps, message .. ": " .. tostring(a) .. " vs " .. tostring(b))
end

local function make_wav(rate, frames)
	local pcm = {}
	for i = 1, frames do
		pcm[i] = string.pack("<i2", math.floor(math.sin(i * 0.1) * 12000))
	end
	local data = table.concat(pcm)
	return "RIFF"
		.. string.pack("<I4", 36 + #data)
		.. "WAVE"
		.. "fmt "
		.. string.pack("<I4I2I2I4I4I2I2", 16, 1, 1, rate, rate * 2, 2, 16)
		.. "data"
		.. string.pack("<I4", #data)
		.. data
end

local function check_rand()
	local a = lubx.Rand.new(42)
	local b = lubx.Rand.new(42)
	local seen = {}
	for _ = 1, 2000 do
		local v = a:between(3, 6)
		expect(v == 3 + b:next_int(4), "between must be min + next_int(max - min + 1)")
		expect(v >= 3 and v <= 6 and v == math.floor(v), "between out of range: " .. tostring(v))
		seen[v] = true
	end
	expect(seen[3] and seen[4] and seen[5] and seen[6], "between must reach both ends")
	expect(a:between(5, 5) == 5, "between(5, 5) must be 5")
	expect(a:between(-2, -1) <= -1, "between must accept negative ranges")
end

local function check_camera()
	local w, h = 1280, 720
	local vp = lubx.Camera3d.vp({
		eye = lubx.Vec3.new(0, 10, -10),
		target = lubx.Vec3.new(0, 0, 0),
		aspect = w / h,
	})
	local c = lubx.Camera3d.project(vp, 0, 0, 0, w, h)
	expect(c ~= nil, "target must project")
	near(c.x, w / 2, 1e-3, "target x is the screen center")
	near(c.y, h / 2, 1e-3, "target y is the screen center")
	expect(c.z > 0 and c.z < 1, "depth must be in (0, 1)")

	local right = lubx.Camera3d.project(vp, 3, 0, 0, w, h)
	local up = lubx.Camera3d.project(vp, 0, 0, 3, w, h)
	expect(right.x > w / 2, "+x is right of center")
	expect(up.y < h / 2, "+z (away from the eye) is above center")

	local behind = lubx.Camera3d.project(vp, 0, 15, -15, w, h)
	expect(behind == nil, "a point behind the camera must not project")

	-- ゲームの床のマス選び: 画面の点 -> レイ -> 床 (y = 0) との交点
	local function pick_floor(cam, sx, sy)
		local ray = lubx.Camera3d.screen_ray(cam, sx, sy, w, h)
		local t = ray:intersect_plane(lubx.Vec3.new(0, 0, 0), lubx.Vec3.new(0, 1, 0))
		return t and ray:at(t)
	end

	local mid = pick_floor(vp, w / 2, h / 2)
	expect(mid ~= nil, "center must hit the ground")
	near(mid.x, 0, 1e-4, "center hit x")
	near(mid.y, 0, 1e-4, "hit is on the ground")
	near(mid.z, 0, 1e-4, "center hit z")

	for _, p in ipairs({ { 3, 2 }, { -5, 7 }, { 1.5, -4 } }) do
		local s = lubx.Camera3d.project(vp, p[1], 0, p[2], w, h)
		local hit = pick_floor(vp, s.x, s.y)
		expect(hit ~= nil, "round trip must hit")
		near(hit.x, p[1], 1e-3, "round trip x")
		near(hit.z, p[2], 1e-3, "round trip z")
	end

	local ray = lubx.Camera3d.screen_ray(vp, w / 2, h / 2, w, h)
	near(ray.dir:length(), 1, 1e-6, "ray dir is normalized")
	near(ray.dir.x, 0, 1e-6, "center ray dir x")
	near(ray.dir.y, -math.sqrt(0.5), 1e-4, "center ray dir y")
	near(ray.dir.z, math.sqrt(0.5), 1e-4, "center ray dir z")

	local level = lubx.Camera3d.vp({
		eye = lubx.Vec3.new(0, 1, 0),
		target = lubx.Vec3.new(0, 1, 10),
		aspect = w / h,
	})
	expect(pick_floor(level, w / 2, h / 2) == nil, "a ray parallel to the ground has no hit")
	expect(pick_floor(level, w / 2, 0) == nil, "a ray above the horizon has no hit")
	local low = pick_floor(level, w / 2, h - 1)
	expect(low ~= nil and low.z > 0, "a ray below the horizon hits ahead of the eye")
end

local function check_ray()
	local ray = lubx.Ray.new(lubx.Vec3.new(1, 5, 2), lubx.Vec3.new(0, -3, 0))
	near(ray.dir.y, -1, 1e-9, "Ray normalizes dir")
	local up = lubx.Vec3.new(0, 1, 0)
	near(ray:intersect_plane(lubx.Vec3.new(0, 2, 0), up), 3, 1e-6, "distance to the plane y = 2")
	local p = ray:at(3)
	near(p.x, 1, 1e-6, "at x")
	near(p.y, 2, 1e-6, "at y")
	expect(ray:intersect_plane(lubx.Vec3.new(0, 9, 0), up) == nil, "a plane behind the origin has no hit")
	expect(ray:intersect_plane(lubx.Vec3.new(0, 0, 0), lubx.Vec3.new(1, 0, 0)) == nil, "a parallel plane has no hit")
	local slanted = lubx.Ray.new(lubx.Vec3.new(0, 1, 0), lubx.Vec3.new(1, -1, 0))
	near(slanted:intersect_plane(lubx.Vec3.new(0, 0, 0), up), math.sqrt(2), 1e-6, "slanted ray distance")
end

local frame = 0
local rt_key = "game_helpers_rt"
local wav_path
local wav_handle
local first_synth
local synth_calls = 0
local synth_first_t, synth_last_u

local function synth_voice()
	local phase = 0
	return function(t, u)
		synth_calls = synth_calls + 1
		synth_first_t = synth_first_t or t
		synth_last_u = u
		phase = phase + 1
		return phase * 10
	end
end

function M.on_init()
	lub.app.config({
		backend = os.getenv("LUB_BACKEND") or "sdlgpu",
		width = 320,
		height = 180,
		resource_sweep_after_frames = SWEEP_FRAMES,
	})
	check_rand()
	check_camera()
	check_ray()
	wav_path = os.tmpname()
	lub.io.save_text(wav_path, make_wav(22050, 441))
end

function M.on_frame()
	frame = frame + 1

	local rt = lubx.Assets.render_target(rt_key, 64, 32, lub.gfx.RGBA8)
	expect(rt ~= nil, "render target must be declared")
	expect(rt.version == 64 * 65536 + 32, "render target version must follow the size")
	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	local nearest = lubx.Assets.render_target("game_helpers_rt2", 8, 8, lub.gfx.RGBA8, lub.gfx.NEAREST, lub.gfx.REPEAT)
	expect(nearest ~= nil, "render target with filter / wrap must be declared")

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	-- wav は毎フレーム宣言し続ける (止めると sweep される)
	local handle = lubx.Assets.wav("game_helpers_wav", wav_path)
	if wav_handle == nil then
		wav_handle = handle
		if frame > 10 then
			fail("wav must become ready within 10 frames")
		end
		return
	end
	expect(handle == wav_handle, "a ready wav must keep returning the same handle")

	if frame == 11 then
		first_synth = lubx.Sfx.synth("game_helpers_synth", 0.01, 1, synth_voice())
		expect(type(first_synth) == "number" and first_synth ~= 0, "synth must return a snd handle")
		expect(synth_calls == 441, "synth must call sample once per sample (" .. synth_calls .. ")")
		expect(synth_first_t == 0, "first sample is at t = 0")
		expect(synth_last_u < 1 and synth_last_u > 0.99, "u must approach 1 at the end")
		local again_synth = lubx.Sfx.synth("game_helpers_synth", 0.01, 1, synth_voice())
		expect(again_synth == first_synth, "the same key / version must return the same handle")
		expect(synth_calls == 441, "the same version must not call sample again")
		lubx.Sfx.synth("game_helpers_synth", 0.01, 2, synth_voice())
		expect(synth_calls == 882, "a new version must rebuild the waveform")
	elseif frame > 11 and frame < 11 + SWEEP_FRAMES + 3 then
		-- 宣言を止めて sweep させる
		return
	elseif frame == 11 + SWEEP_FRAMES + 3 then
		local rebuilt = lubx.Sfx.synth("game_helpers_synth", 0.01, 2, synth_voice())
		expect(rebuilt ~= nil and rebuilt ~= 0, "a swept synth must be recreated from the cache")
		expect(synth_calls == 882, "recreating a swept synth must not call sample")
		os.remove(wav_path)
		print("LUBX_GAME_HELPERS_OK")
		lub.app.quit()
	end
end

return M
