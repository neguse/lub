local M = {}

local wrote = false
local rb = nil

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("test")
end

function M.on_frame()
	if wrote then
		lub.quit()
		return
	end

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

	local st0 = rb:read_texture(tex, 30)
	assert(st0 == "processing", "first read_texture should be processing")

	local st1, bytes, w, h, fmt, stride, id = rb:read_texture(tex, 31)
	assert(st1 == "ready", "read_texture result was not ready")
	assert(bytes ~= nil, "read_texture returned nil")
	assert(w == 4 and h == 4, "read_texture returned wrong size")
	assert(fmt == lub.gfx.RGBA8, "read_texture returned non-lub.gfx.RGBA8")
	assert(stride == 16, "read_texture returned wrong stride")
	assert(bytes.length == 64, "read_texture returned wrong byte length")
	assert(id == 30, "read_texture returned wrong id")

	local st2, bytes2, w2, h2, fmt2, stride2, id2 = rb:read_texture(tex)
	assert(st2 == "ready", "read_texture did not enqueue while returning ready")
	assert(bytes2 ~= nil, "second read_texture returned nil")
	assert(w2 == 4 and h2 == 4, "second read_texture returned wrong size")
	assert(fmt2 == lub.gfx.RGBA8, "second read_texture returned non-lub.gfx.RGBA8")
	assert(stride2 == 16, "second read_texture returned wrong stride")
	assert(bytes2.length == 64, "second read_texture returned wrong byte length")
	assert(id2 == 31, "second read_texture returned wrong id")

	local out = os.getenv("LUB_READ_TEXTURE_TEST_OUT") or "/tmp/lub_read_texture_test.png"
	lub.png.write(out, bytes, w, h, stride)
	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	wrote = true
	lub.quit()
end

return M
