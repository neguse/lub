-- tests/lua/test_texture_recording_order.lua
-- 1 フレームの中で同じ key の texture を違う画素で宣言し直し、それぞれの後に
-- draw で sample する。draw は宣言を記録した順の内容を読むので、赤で宣言した
-- 後の左半分は赤、緑で宣言し直した後の右半分は緑になる。書き込みが記録順を
-- 追い越す backend では両方とも最後の緑になる。
-- - rewrite: 同じ大きさで書き直す。
-- - resize: 大きさの違う画素で宣言し直し、draw が使った texture を作り直す。
-- 加えて、描いた直後の render target を大きさを変えて宣言し直す。resize と
-- これは GPU が使う前の資源を消すので、消すのが早すぎる backend では
-- validation error になる。

local M = {}

local CASES = { "rewrite", "resize" }
local rbs = {}
local done = {}
local mismatches = {}
local frames = 0
local requested = false

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	for _, name in ipairs(CASES) do
		rbs[name] = lub.gfx.readback("texture_recording_order_" .. name)
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

local RED = { 255, 0, 0, 255 }
local GREEN = { 0, 255, 0, 255 }

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "test_texture_recording_order: readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_texture_recording_order.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_texture_recording_order.fs.slang")
	if not vs or not fs then
		return
	end
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local tex_opts = { filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	local opts = { shader = sh, depth = false, cull = lub.gfx.NONE }
	local before, after = frames * 2, frames * 2 + 1

	local rts = {}
	for _, name in ipairs(CASES) do
		rts[name] = lub.gfx.use_texture("rt_" .. name, 4, 4, lub.gfx.RGBA8, nil, 1, {
			target = true,
			filter = lub.gfx.NEAREST,
			wrap = lub.gfx.CLAMP,
		})
	end

	lub.gfx.begin_pass({ target = rts.rewrite, clear_color = { 0, 0, 0, 1 } })
	local tex = lub.gfx.use_texture("tex", 1, 1, lub.gfx.RGBA8, RED, before, tex_opts)
	lub.gfx.draw(6, { tex = tex, uniforms = { side = { 0, 0, 0, 0 } } }, opts)
	tex = lub.gfx.use_texture("tex", 1, 1, lub.gfx.RGBA8, GREEN, after, tex_opts)
	lub.gfx.draw(6, { tex = tex, uniforms = { side = { 1, 0, 0, 0 } } }, opts)
	lub.gfx.end_pass()

	lub.gfx.begin_pass({ target = rts.resize, clear_color = { 0, 0, 0, 1 } })
	local sized = lub.gfx.use_texture("sized", 1, 1, lub.gfx.RGBA8, RED, before, tex_opts)
	lub.gfx.draw(6, { tex = sized, uniforms = { side = { 0, 0, 0, 0 } } }, opts)
	local green4 = {}
	for _ = 1, 4 do
		for _, v in ipairs(GREEN) do
			green4[#green4 + 1] = v
		end
	end
	sized = lub.gfx.use_texture("sized", 2, 2, lub.gfx.RGBA8, green4, after, tex_opts)
	lub.gfx.draw(6, { tex = sized, uniforms = { side = { 1, 0, 0, 0 } } }, opts)
	lub.gfx.end_pass()

	local target_opts = { target = true, filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	local target = lub.gfx.use_texture("target", 2, 2, lub.gfx.RGBA8, nil, before, target_opts)
	lub.gfx.begin_pass({ target = target, clear_color = { 0, 0, 1, 1 } })
	lub.gfx.end_pass()
	lub.gfx.use_texture("target", 4, 4, lub.gfx.RGBA8, nil, after, target_opts)

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
						"test_texture_recording_order %s: left (%d, %d, %d) want (255, 0, 0), right (%d, %d, %d) want (0, 255, 0)",
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
		print("OK test_texture_recording_order")
		lub.app.quit()
	end
end

return M
