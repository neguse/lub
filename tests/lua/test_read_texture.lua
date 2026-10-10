local M = {}

local wrote = false
local rb = nil
local frames = 0
-- 0: id 30 を積む / 1: id 31 を 1 回だけ渡す (30 が届いていればその呼び出しが
-- 30 を返し、まだなら 31 が 30 の後ろに積まれる) / 2: 30 を待つ /
-- 3: 31 を待つ。結果は早くても次のフレームの poll で届く。
local stage = 0

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("test")
end

local function expect_result(bytes, w, h, fmt, stride, id, want_id)
	assert(bytes ~= nil, "read_texture returned nil")
	assert(w == 4 and h == 4, "read_texture returned wrong size")
	assert(fmt == lub.gfx.RGBA8, "read_texture returned non-lub.gfx.RGBA8")
	assert(stride == 16, "read_texture returned wrong stride")
	assert(bytes.length == 64, "read_texture returned wrong byte length")
	assert(id == want_id, "read_texture returned wrong id")
end

function M.on_frame()
	if wrote then
		lub.app.quit()
		return
	end
	frames = frames + 1
	assert(frames < 300, "read_texture never became ready")

	local tex = lub.gfx.use_texture(
		"readback_rt",
		4,
		4,
		lub.gfx.RGBA8,
		nil,
		1,
		{ target = true, filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	)
	lub.gfx.begin_pass({ target = tex, clear_color = { 1.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()

	if stage == 0 then
		local st0 = rb:read_texture(tex, 30)
		assert(st0 == "processing", "first read_texture should be processing")
		stage = 1
		return
	end
	if stage == 1 then
		local st1, bytes, w, h, fmt, stride, id = rb:read_texture(tex, 31)
		assert(st1 == "ready" or st1 == "processing", "unexpected read_texture status " .. tostring(st1))
		if st1 == "ready" then
			expect_result(bytes, w, h, fmt, stride, id, 30)
			stage = 3
		else
			stage = 2
		end
		return
	end
	if stage == 2 then
		local st, bytes, w, h, fmt, stride, id = rb:read_texture(tex)
		if st == "ready" then
			expect_result(bytes, w, h, fmt, stride, id, 30)
			stage = 3
		end
		return
	end
	-- 31 が積まれていなければ、ここが ready にならず frames の上限で落ちる
	local st2, bytes, w, h, fmt, stride, id = rb:read_texture(tex)
	if st2 ~= "ready" then
		return
	end
	expect_result(bytes, w, h, fmt, stride, id, 31)

	local out = os.getenv("LUB_READ_TEXTURE_TEST_OUT") or "/tmp/lub_read_texture_test.png"
	lub.png.write(out, bytes, w, h, stride)
	wrote = true
	lub.app.quit()
end

return M
