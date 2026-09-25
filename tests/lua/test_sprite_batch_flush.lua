-- lubx.SpriteBatch の flush:
--   * 1 つの pass で Begin() から積み直して何度 flush しても、どの flush も
--     そのとき積んだ sprite を描く (後の flush が前の flush の頂点を上書きしない)
--   * instanced と instanced でない (1 sprite 6 頂点) の両方で同じ
-- offscreen の target に描き、読み戻した画素で確かめる。
local x = dofile("samples/lubx.lua")

local W, H = 64, 16
local f = 0
local rb
local step = 1
local requested = nil
local backend = os.getenv("LUB_BACKEND") or "sdlgpu"

local RED = { 1, 0, 0 }
local BLUE = { 0, 0, 1 }
local GREEN = { 0, 1, 0 }

local function fail(message)
	print("SPRITE_BATCH_FLUSH_FAIL frame " .. f .. " step " .. step .. ": " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function target()
	return lub.gfx.use_texture("sbf_rt", W, H, lub.gfx.RGBA8, nil, 1, { target = true })
end

local function rect(sb, x0, w, col)
	sb:rect(x0, 0, w, H, x.Color.rgb(col[1], col[2], col[3]))
end

-- 左から赤・青を 1 回目の flush で、緑を 2 回目の flush で描く
local function draw_two_flushes(sb)
	lub.gfx.begin_pass({ target = target(), clear_color = { 0, 0, 0, 1 } })
	sb:begin()
	rect(sb, 0, 16, RED)
	rect(sb, 16, 16, BLUE)
	sb:flush()
	sb:begin()
	rect(sb, 32, 32, GREEN)
	sb:flush()
	lub.gfx.end_pass()
end

local steps = {
	{
		name = "instanced: two flushes in one pass",
		batch = function()
			return x.SpriteBatch.new(W, H, "sbf_inst", "sbf_inst", true)
		end,
	},
	{
		name = "not instanced: two flushes in one pass",
		batch = function()
			return x.SpriteBatch.new(W, H, "sbf_legacy", "sbf_legacy", false)
		end,
	},
}

local function check_column(bytes, stride, px, col, what)
	for py = 0, H - 1 do
		local o = py * stride + px * 4
		local r, g, b = bytes[o + 1], bytes[o + 2], bytes[o + 3]
		local want = { col[1] * 255, col[2] * 255, col[3] * 255 }
		expect(
			math.abs(r - want[1]) <= 2 and math.abs(g - want[2]) <= 2 and math.abs(b - want[3]) <= 2,
			string.format(
				"%s: pixel (%d, %d) is (%d, %d, %d), want (%d, %d, %d)",
				what,
				px,
				py,
				r,
				g,
				b,
				want[1],
				want[2],
				want[3]
			)
		)
	end
end

return {
	on_init = function()
		lub.config({ backend = backend, width = 64, height = 64 })
		rb = lub.gfx.readback("sbf_rb")
	end,
	on_frame = function()
		f = f + 1
		if step > #steps then
			print("SPRITE_BATCH_FLUSH_OK steps=" .. #steps)
			lub.quit()
			return
		end
		local s = steps[step]
		local request = nil
		if not requested then
			if not s.sb then
				s.sb = s.batch()
			end
			draw_two_flushes(s.sb)
			requested = 100 + step
			request = requested
		end
		local status, bytes, _, _, _, stride, id = rb:read_texture(target(), request)
		expect(status ~= "error", "readback failed")
		if status == "ready" and id == requested then
			check_column(bytes, stride, 8, RED, s.name .. ", first flush")
			check_column(bytes, stride, 24, BLUE, s.name .. ", first flush")
			check_column(bytes, stride, 48, GREEN, s.name .. ", second flush")
			step = step + 1
			requested = nil
		end
		expect(f < 300, "readback for step " .. step .. " did not arrive")
	end,
}
