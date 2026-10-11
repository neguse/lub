-- tests/lua/test_many_draws.lua
-- 1 フレームに 20,000 回 draw し、各 draw が自分の texture と uniform で描くこと。
-- draw k は画素 k を 1 つ塗る。R, G は texture (k % 256 番目)、B は fragment の
-- uniform (k % 251)、位置は vertex の uniform で決まる。descriptor が尽きて
-- 直前の draw の table を使うと色か位置がずれ、draw を飛ばすと画素が黒のまま残る。

local M = {}

local W, H = 200, 100
local N = W * H
local NTEX = 256
local NTINT = 251

local rb = nil
local frames = 0
local requested = false
local cells = {}
local tints = {}
local filters = {}
local wraps = {}

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("many_draws")
	for k = 0, N - 1 do
		cells[k] = { k % W, k // W, W, H }
		tints[k] = { (k % NTINT) / 255, 0, 0, 0 }
	end
	filters = { lub.gfx.NEAREST, lub.gfx.LINEAR }
	wraps = { lub.gfx.CLAMP, lub.gfx.REPEAT }
end

function M.on_event(e) end
function M.on_quit() end

local function fail(message)
	print("FAIL " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "test_many_draws: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_many_draws.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_many_draws.fs.slang")
	if not vs or not fs then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local texs = {}
	for t = 0, NTEX - 1 do
		-- filter と wrap を texture ごとに変える (1x1 なので sample の結果は同じ)
		texs[t] = lub.gfx.use_texture("tex" .. t, 1, 1, lub.gfx.RGBA8, { t, 255 - t, 0, 255 }, 1, {
			filter = filters[t % 2 + 1],
			wrap = wraps[(t // 2) % 2 + 1],
		})
	end
	local rt = lub.gfx.use_texture("rt", W, H, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})
	local opts = { shader = sh, depth = false, cull = lub.gfx.NONE }

	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 0 } })
	for k = 0, N - 1 do
		lub.gfx.draw(6, { tex = texs[k % NTEX], uniforms = { cell = cells[k], tint = tints[k] } }, opts)
	end
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	local st, bytes, w, h, _, stride, id, _, err
	if not requested then
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt, 1)
		requested = true
	else
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt)
	end
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		return
	end
	expect(id == 1 and w == W and h == H, "unexpected readback result")
	for k = 0, N - 1 do
		local off = (k // W) * stride + (k % W) * 4
		local t = k % NTEX
		local r, g, b, a = bytes:get(off), bytes:get(off + 1), bytes:get(off + 2), bytes:get(off + 3)
		if r ~= t or g ~= 255 - t or b ~= k % NTINT or a ~= 255 then
			fail(
				string.format(
					"test_many_draws: draw %d of %d wrote (%d, %d, %d, %d), want (%d, %d, %d, 255)",
					k,
					N,
					r,
					g,
					b,
					a,
					t,
					255 - t,
					k % NTINT
				)
			)
		end
	end
	print("OK test_many_draws")
	lub.app.quit()
end

return M
