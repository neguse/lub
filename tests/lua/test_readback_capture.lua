-- tests/lua/test_readback_capture.lua
-- 同じフレームで main_tex を clear → render target の pass → read_texture(rt)
-- → main_tex に LOAD の pass、の後の capture。読み戻しの後も
-- main_tex は同じ image のままで、capture はこのフレームの clear の色になる。
-- clear の色はフレームごとに変え、前のフレームの image が写ると色がずれる。
--
-- --capture <path> と同じ path を LUB_READBACK_CAPTURE_TEST_OUT に渡して走らせる。
-- capture の後に app が終わるので、on_quit で capture の PNG を確かめる。

local M = {}

local OUT = os.getenv("LUB_READBACK_CAPTURE_TEST_OUT")
local RT_COLOR = { 0, 0, 255, 255 }
local rb
local frame = 0
local want
local rt_verified = 0

local function fail(message)
	print("READBACK_CAPTURE_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function clear_color(k)
	local r = (k % 5) * 0.25
	return { r, 1.0 - r, 0.0, 1.0 }
end

function M.on_init()
	expect(OUT, "LUB_READBACK_CAPTURE_TEST_OUT is not set")
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("rt")
end

function M.on_frame()
	frame = frame + 1
	expect(frame < 300, "capture never happened")
	local c = clear_color(frame)
	want = { math.floor(c[1] * 255 + 0.5), math.floor(c[2] * 255 + 0.5), 0, 255 }

	local rt = lub.gfx.use_texture("rt", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true, filter = lub.gfx.NEAREST })

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = c })
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = rt, clear_color = { 0.0, 0.0, 1.0, 1.0 } })
	lub.gfx.end_pass()

	local st, bytes, _, _, _, _, _, _, err = rb:read_texture(rt, frame)
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st == "ready" then
		for i = 1, 4 do
			expect(bytes:get(i - 1) == RT_COLOR[i], string.format("rt byte %d: got %d", i, bytes:get(i - 1)))
		end
		rt_verified = rt_verified + 1
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, load = lub.gfx.LOAD })
	lub.gfx.end_pass()
end

function M.on_quit()
	expect(want, "no frame was rendered")
	expect(rt_verified > 0, "no read_texture(rt) result arrived")
	local bytes, w, h, _, stride, _, status, err = lub.png.load(OUT)
	expect(status == "ready" and bytes, string.format("capture %s not loaded: %s %s", OUT, status, tostring(err)))
	-- 中央と四隅
	local points = { { w // 2, h // 2 }, { 0, 0 }, { w - 1, 0 }, { 0, h - 1 }, { w - 1, h - 1 } }
	for _, p in ipairs(points) do
		local off = p[2] * stride + p[1] * 4
		for i = 1, 4 do
			local got = bytes:get(off + i - 1)
			expect(
				math.abs(got - want[i]) <= 2,
				string.format(
					"capture (%d, %d) channel %d: got %d, want %d (frame %d)",
					p[1],
					p[2],
					i,
					got,
					want[i],
					frame
				)
			)
		end
	end
	print("OK test_readback_capture")
end

return M
