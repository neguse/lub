-- tests/lua/test_buffer_recording_order.lua
-- 1 フレームの中で同じ key の buffer を違う内容で宣言し直し、それぞれの後に
-- draw する。draw は宣言を記録した順の内容を読むので、どの case も左半分は
-- 宣言し直す前の内容、右半分は後の内容で描かれ、左が赤、右が緑になる。
-- 書き込みが記録順を追い越す backend では左も後の内容で描かれる。
-- - storage: draw が読む STORAGE buffer を書き直す。
-- - index: INDEX buffer を書き直す。前の index は左の quad、後は右の quad を
--   指す。
-- - compute: compute が赤を書いた buffer を、draw の後に CPU が緑で書き直す。
-- - resize: 大きさの違う内容で宣言し直し、draw が使った buffer を作り直す。

local M = {}

local CASES = { "storage", "index", "compute", "resize" }
local rbs = {}
local done = {}
local mismatches = {}
local frames = 0
local requested = false

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	for _, name in ipairs(CASES) do
		rbs[name] = lub.gfx.readback("buffer_recording_order_" .. name)
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

local function pixel(bytes, stride, x)
	local off = stride + x * 4
	return bytes:get(off), bytes:get(off + 1), bytes:get(off + 2)
end

local LEFT = { side = { 0, 0, 0, 0 } }
local RIGHT = { side = { 1, 0, 0, 0 } }

local function quad_indices(first)
	local t = {}
	for i = 0, 5 do
		t[#t + 1] = first + i
	end
	return t
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "test_buffer_recording_order: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_buffer_recording_order.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_buffer_recording_order.fs.slang")
	local cs, ver_cs = lub.io.load_text("tests/lua/test_buffer_recording_order.cs.slang")
	if not vs or not fs or not cs then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local sh_c = lub.gfx.use_shader_compute("cs", cs, ver_cs)
	local opts = { shader = sh, depth = false, cull = lub.gfx.NONE }
	local before, after = frames * 2, frames * 2 + 1
	local red = lub.gfx.use_buffer("red", lub.gfx.STORAGE, { 1, 0, 0, 1 }, 1)
	local green = lub.gfx.use_buffer("green", lub.gfx.STORAGE, { 0, 1, 0, 1 }, 1)

	local rts = {}
	for _, name in ipairs(CASES) do
		rts[name] = lub.gfx.use_texture("rt_" .. name, 4, 4, lub.gfx.RGBA8, nil, 1, {
			target = true,
			filter = lub.gfx.NEAREST,
			wrap = lub.gfx.CLAMP,
		})
	end

	lub.gfx.begin_pass({ target = rts.storage, clear_color = { 0, 0, 0, 1 } })
	local col = lub.gfx.use_buffer("col", lub.gfx.STORAGE, { 1, 0, 0, 1 }, before)
	lub.gfx.draw(6, { col = col, uniforms = LEFT }, opts)
	col = lub.gfx.use_buffer("col", lub.gfx.STORAGE, { 0, 1, 0, 1 }, after)
	lub.gfx.draw(6, { col = col, uniforms = RIGHT }, opts)
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = rts.index, clear_color = { 0, 0, 0, 1 } })
	local idx = lub.gfx.use_buffer("idx", lub.gfx.INDEX, quad_indices(0), before)
	lub.gfx.draw(6, { indices = idx, col = red, uniforms = LEFT }, opts)
	idx = lub.gfx.use_buffer("idx", lub.gfx.INDEX, quad_indices(6), after)
	lub.gfx.draw(6, { indices = idx, col = green, uniforms = LEFT }, opts)
	lub.gfx.end_pass()

	local out = lub.gfx.use_buffer("out", lub.gfx.STORAGE, { 0, 0, 0, 1 }, before)
	lub.gfx.dispatch(1, 1, 1, { col = out }, { shader = sh_c })
	lub.gfx.begin_pass({ target = rts.compute, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.draw(6, { col = out, uniforms = LEFT }, opts)
	out = lub.gfx.use_buffer("out", lub.gfx.STORAGE, { 0, 1, 0, 1 }, after)
	lub.gfx.draw(6, { col = out, uniforms = RIGHT }, opts)
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = rts.resize, clear_color = { 0, 0, 0, 1 } })
	local sized = lub.gfx.use_buffer("sized", lub.gfx.STORAGE, { 1, 0, 0, 1 }, before)
	lub.gfx.draw(6, { col = sized, uniforms = LEFT }, opts)
	sized = lub.gfx.use_buffer("sized", lub.gfx.STORAGE, { 0, 1, 0, 1, 0, 0, 0, 0 }, after)
	lub.gfx.draw(6, { col = sized, uniforms = RIGHT }, opts)
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	local all_done = true
	for _, name in ipairs(CASES) do
		if not done[name] then
			local st, bytes, w, h, _, stride, id, _, err
			if not requested then
				st, bytes, w, h, _, stride, id, _, err = rbs[name]:read_texture(rts[name], 1)
			else
				st, bytes, w, h, _, stride, id, _, err = rbs[name]:read_texture(rts[name])
			end
			expect(st ~= "error", name .. ": read_texture failed: " .. tostring(err))
			if st == "ready" then
				expect(id == 1 and w == 4 and h == 4, name .. ": unexpected readback result")
				local lr, lg, lb = pixel(bytes, stride, 1)
				local rr, rg, rb = pixel(bytes, stride, 2)
				if not (lr == 255 and lg == 0 and lb == 0 and rr == 0 and rg == 255 and rb == 0) then
					mismatches[#mismatches + 1] = string.format(
						"test_buffer_recording_order %s: left (%d, %d, %d) want (255, 0, 0), right (%d, %d, %d) want (0, 255, 0)",
						name,
						lr,
						lg,
						lb,
						rr,
						rg,
						rb
					)
				end
				done[name] = true
			else
				all_done = false
			end
		end
	end
	requested = true
	if all_done then
		expect(#mismatches == 0, table.concat(mismatches, "; "))
		print("OK test_buffer_recording_order")
		lub.app.quit()
	end
end

return M
