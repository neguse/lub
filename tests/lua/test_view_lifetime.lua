-- runtime の memory を指す view (Bytes と数値配列) は、返した frame の終わりまで
-- 有効。同じ frame の中で同じ API を呼び直しても、前の呼び出しの view は前の
-- 中身のまま読める。次の frame に持ち越した view は読むと stale の error になる。
local M = {}

local FONT_PATH = "samples/data/fonts/MPLUS1p-subset.ttf"

local function fail(message)
	print("VIEW_LIFETIME_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

-- view の中身を Lua の table に写す (比較用)
local function copy(view)
	local out = {}
	for i = 1, #view do
		out[i] = view[i]
	end
	return out
end

local function same(view, expected, what)
	expect(#view == #expected, what .. ": length changed " .. #expected .. " -> " .. #view)
	for i = 1, #expected do
		if view[i] ~= expected[i] then
			fail(what .. ": element " .. i .. " changed " .. tostring(expected[i]) .. " -> " .. tostring(view[i]))
		end
	end
end

local function cp(s)
	return (utf8.codepoint(s))
end

local function sphere_grid(n, r)
	local grid = {}
	local c = (n - 1) / 2
	for z = 0, n - 1 do
		for y = 0, n - 1 do
			for x = 0, n - 1 do
				local dx, dy, dz = x - c, y - c, z - c
				grid[#grid + 1] = math.sqrt(dx * dx + dy * dy + dz * dz) - r
			end
		end
	end
	return grid
end

local function physics_world(key, x)
	local world = lub.phys2d.world(key, { gravity = { x = 0, y = 0 }, fixed_dt = 1 / 60 })
	world:begin()
	local body = world:body("body", { type = lub.phys2d.STATIC, initial = { x = x, y = 0 } })
	body:box("box", { hx = 1, hy = 1, density = 0 })
	world:step(1 / 60)
	return world
end

local held = {}

-- frame 1: 同じ API を続けて呼び、前の view が前の中身のまま読めること
local function first_frame()
	local fh = io.open(FONT_PATH, "rb")
	expect(fh ~= nil, "font file missing: " .. FONT_PATH)
	local ttf = fh:read("a")
	fh:close()

	local glyph = lub.font.glyph(ttf, cp("い"), 32)
	local glyph_bytes = tostring(glyph.bytes)
	lub.font.glyph(ttf, cp("ほ"), 64)
	expect(tostring(glyph.bytes) == glyph_bytes, "font.glyph: earlier bytes changed by the next call")
	held.bytes = glyph.bytes

	local mesh = lub.font.glyph_mesh(ttf, cp("ほ"))
	local positions = copy(mesh.positions)
	local indices = copy(mesh.indices)
	lub.font.glyph_mesh(ttf, cp("い"))
	lub.font.glyph_mesh(ttf, cp("ほ"), 0.0005)
	same(mesh.positions, positions, "font.glyph_mesh positions")
	same(mesh.indices, indices, "font.glyph_mesh indices")
	held.positions = mesh.positions

	local nets = lub.mesh.surface_nets(sphere_grid(8, 2.5), 8, 8, 8)
	local nets_positions = copy(nets.positions)
	lub.mesh.surface_nets(sphere_grid(12, 4.5), 12, 12, 12)
	same(nets.positions, nets_positions, "mesh.surface_nets positions")

	local small = lub.mesh.sdf_mesh({ { op = lub.mesh.SPHERE, params = { 0.3 } } }, 0, 16)
	local small_min = copy(small.bounds_min)
	local small_positions = copy(small.positions)
	lub.mesh.sdf_mesh({ { op = lub.mesh.BOX, params = { 0.8, 0.5, 0.6 } } }, 0, 16)
	same(small.bounds_min, small_min, "mesh.sdf_mesh bounds_min")
	same(small.positions, small_positions, "mesh.sdf_mesh positions")

	local left = physics_world("view_left", -3)
	local right = physics_world("view_right", 3)
	local dbg = left:debug({ shapes = true })
	expect(#dbg.polygons > 0, "phys2d debug: no polygons")
	local polygons = copy(dbg.polygons)
	right:debug({ shapes = true, bounds = true })
	same(dbg.polygons, polygons, "phys2d debug polygons")

	local root = os.tmpname()
	os.remove(root)
	local path = root .. ".txt"
	lub.io.save_text(path, "first content")
	local bytes = lub.io.load_bytes(path)
	lub.io.save_text(path, "second content, longer than the first")
	local replaced = lub.io.load_bytes(path)
	expect(tostring(replaced) == "second content, longer than the first", "io.load_bytes: reload missing")
	expect(tostring(bytes) == "first content", "io.load_bytes: earlier bytes changed by the reload")
	os.remove(path)
end

-- frame 2: 前の frame の view は読むと stale の error
local function expect_stale(f, what)
	local ok, err = pcall(f)
	expect(not ok, what .. ": reading a view from an earlier frame must error")
	expect(tostring(err):find("stale view", 1, true) ~= nil, what .. ": unexpected error " .. tostring(err))
end

local function second_frame()
	expect_stale(function()
		return held.bytes[1]
	end, "bytes index")
	expect_stale(function()
		return #held.bytes
	end, "bytes length")
	expect_stale(function()
		return tostring(held.bytes)
	end, "bytes tostring")
	expect_stale(function()
		return held.positions[1]
	end, "positions index")
	expect_stale(function()
		return held.positions:get(0)
	end, "positions get")
end

local frame = 0

function M.on_frame()
	frame = frame + 1
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	if frame == 1 then
		first_frame()
	elseif frame == 2 then
		second_frame()
		print("VIEW_LIFETIME_OK")
		lub.app.quit()
	end
end

return M
