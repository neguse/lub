-- tests/lua/test_many_draws.lua
-- 1 フレームに 20,000 回 draw し、各 draw が自分の texture、sampler、uniform で
-- 描くこと。draw k は画素 k を 1 つ塗る:
-- - R, G は 2 つの texture (k % 256 番目と (k + 2) % 256 番目) を引いた値で、
--   texture ごとの (filter, wrap) で決まる。sampler の取り違えで値が変わる。
-- - A は 1 つ目の texture の番号、B は fragment の uniform (k % 251)、位置は
--   vertex の uniform で決まる。
-- descriptor が尽きて直前の draw の table を使うと色か位置がずれ、draw を
-- 飛ばすと画素が黒のまま残る。
-- draw の前に compute を 2,500 回 dispatch し (各 dispatch が読む buffer と
-- 書く buffer を 1 つずつ持つ)、数え上げた結果を最後の行の画素に描く。
-- 重いフレームの後に軽いフレームを挟み、もう一度重いフレームを確かめる
-- (Vulkan は軽いフレームで余った descriptor pool を返す)。

local M = {}

local W, H = 200, 100
local N = W * H
local NTEX = 256
local NTINT = 251
local NDISPATCH = 2500
local NLIGHT = 16
local LIGHT_FRAMES = 4
local HEAVY_FRAMES = 2

local rb = nil
local frames = 0
local cells = {}
local tints = {}
local codes = {}
local counter_cell = { 0, H, W, H + 1 }

local heavy_done = 0
local pending = nil
local light_left = 0

-- texture t の (filter, wrap) と、fs の PROBE が返す値 (0..255)。
local function sampler_of(t)
	local nearest = t % 2 == 0
	local clamp = (t // 2) % 2 == 0
	return nearest, clamp
end

local function probe(t)
	local nearest, clamp = sampler_of(t)
	local between = nearest and 255 or 0.75 * 255
	local outside = clamp and 255 or (nearest and 0 or 0.25 * 255)
	return 0.25 * between + 0.75 * outside
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("many_draws")
	for k = 0, N - 1 do
		cells[k] = { k % W, k // W, W, H + 1 }
		tints[k] = { (k % NTINT) / 255, 0, 0, 0 }
	end
	for t = 0, NTEX - 1 do
		codes[t] = probe(t)
	end
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

local function near(v, want)
	return math.abs(v - want) <= 3
end

local function verify(bytes, stride)
	for k = 0, N - 1 do
		local off = (k // W) * stride + (k % W) * 4
		local t, t2 = k % NTEX, (k + 2) % NTEX
		local r, g, b, a = bytes:get(off), bytes:get(off + 1), bytes:get(off + 2), bytes:get(off + 3)
		if not near(r, codes[t]) or not near(g, codes[t2]) or b ~= k % NTINT or a ~= t then
			fail(
				string.format(
					"test_many_draws: draw %d of %d wrote (%d, %d, %d, %d), want (%.0f, %.0f, %d, %d)",
					k,
					N,
					r,
					g,
					b,
					a,
					codes[t],
					codes[t2],
					k % NTINT,
					t
				)
			)
		end
	end
	local off = H * stride
	local n = bytes:get(off) + bytes:get(off + 1) * 256
	local last = bytes:get(off + 2) + bytes:get(off + 3) * 256
	expect(
		n == NDISPATCH and last == NDISPATCH - 1,
		string.format(
			"test_many_draws: %d dispatches counted %d (last step %d), want %d (last step %d)",
			NDISPATCH,
			n,
			last,
			NDISPATCH,
			NDISPATCH - 1
		)
	)
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 600, "test_many_draws: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_many_draws.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_many_draws.fs.slang")
	local cs, ver_cs = lub.io.load_text("tests/lua/test_many_draws.cs.slang")
	local counter_fs, ver_counter = lub.io.load_text("tests/lua/test_many_draws_counter.fs.slang")
	if not (vs and fs and cs and counter_fs) then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local sh_counter = lub.gfx.use_shader("counter", vs, counter_fs, ver_vs ~ ver_counter)
	local sh_cs = lub.gfx.use_shader_compute("cs", cs, ver_cs)
	local texs = {}
	for t = 0, NTEX - 1 do
		local nearest, clamp = sampler_of(t)
		texs[t] = lub.gfx.use_texture("tex" .. t, 2, 1, lub.gfx.RGBA8, { 0, t, 0, 255, 255, t, 0, 255 }, 1, {
			filter = nearest and lub.gfx.NEAREST or lub.gfx.LINEAR,
			wrap = clamp and lub.gfx.CLAMP or lub.gfx.REPEAT,
		})
	end
	local counts = {
		[0] = lub.gfx.use_buffer("count0", lub.gfx.STORAGE, { 0, 0, 0, 0 }, 1),
		lub.gfx.use_buffer("count1", lub.gfx.STORAGE, { 0, 0, 0, 0 }, 1),
	}
	local rt = lub.gfx.use_texture("rt", W, H + 1, lub.gfx.RGBA8, nil, 1, {
		target = true,
		filter = lub.gfx.NEAREST,
		wrap = lub.gfx.CLAMP,
	})
	local opts = { shader = sh, depth = false, cull = lub.gfx.NONE }

	local heavy = pending == nil and light_left == 0
	if heavy then
		for i = 0, NDISPATCH - 1 do
			lub.gfx.dispatch(1, 1, 1, {
				src = counts[i % 2],
				dst = counts[(i + 1) % 2],
				uniforms = { step = { i, 0, 0, 0 } },
			}, { shader = sh_cs })
		end
	end
	lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 0 } })
	for k = 0, (heavy and N or NLIGHT) - 1 do
		lub.gfx.draw(6, {
			tex = texs[k % NTEX],
			tex2 = texs[(k + 2) % NTEX],
			uniforms = { cell = cells[k], tint = tints[k] },
		}, opts)
	end
	if heavy then
		lub.gfx.draw(
			6,
			{ counter = counts[NDISPATCH % 2], uniforms = { cell = counter_cell } },
			{ shader = sh_counter, depth = false, cull = lub.gfx.NONE }
		)
	end
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	local st, bytes, w, h, _, stride, id, _, err
	if heavy then
		pending = heavy_done + 1
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt, pending)
	else
		light_left = math.max(light_left - 1, 0)
		st, bytes, w, h, _, stride, id, _, err = rb:read_texture(rt)
	end
	expect(st ~= "error", "read_texture failed: " .. tostring(err))
	if st ~= "ready" then
		return
	end
	expect(id == pending and w == W and h == H + 1, "unexpected readback result")
	verify(bytes, stride)
	heavy_done = pending
	pending = nil
	if heavy_done == HEAVY_FRAMES then
		print("OK test_many_draws")
		lub.app.quit()
		return
	end
	light_left = LIGHT_FRAMES
end

return M
