-- tests/lua/test_instance_count_zero.lua
-- draw の instance_count に 0 以下を渡すと draw 自体が skip される
-- (stub の記述どおり)。1 なら描く。render target に描いて読み戻して確かめる。

local M = {}

local rb = nil
local done = false

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("test")
end

local function pixel(bytes, stride, x, y)
	local o = y * stride + x * 4
	return bytes[o + 1], bytes[o + 2], bytes[o + 3], bytes[o + 4]
end

-- 描いて読み戻す。queue は FIFO なので、渡した id の結果が出るまで読む。
local function render(shader, verts, instance_count, id)
	local tex = lub.gfx.use_texture("ic_rt", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	lub.gfx.begin_pass({ target = tex, clear_color = { 1.0, 0.0, 0.0, 1.0 } })
	lub.gfx.draw(3, { verts = verts }, { shader = shader, cull = lub.gfx.NONE, instance_count = instance_count })
	lub.gfx.end_pass()
	for _ = 1, 16 do
		local st, bytes, w, h, fmt, stride, rid = rb:read_texture(tex, id)
		if st == "ready" and rid == id then
			assert(w == 4 and h == 4 and fmt == lub.gfx.RGBA8, "unexpected readback shape")
			return pixel(bytes, stride, 1, 1)
		end
	end
	error("readback " .. id .. " never became ready")
end

function M.on_frame()
	if done then
		lub.app.quit()
		return
	end
	local vs, ver_vs = lub.io.load_text("tests/lua/test_instance_count_zero.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_instance_count_zero.fs.slang")
	if not vs or not fs then
		return
	end
	local s = lub.gfx.use_shader("ic_sh", vs, fs, ver_vs ~ ver_fs)
	-- 画面全体を覆う三角形
	local verts = lub.gfx.use_buffer("ic_vb", lub.gfx.STORAGE, { -1, -1, 3, -1, -1, 3 }, 1)

	local r, g, b, a = render(s, verts, 0, 1)
	assert(
		r == 255 and g == 0 and b == 0 and a == 255,
		string.format("instance_count = 0 must not draw (got %d,%d,%d,%d)", r, g, b, a)
	)
	r, g, b, a = render(s, verts, -3, 2)
	assert(
		r == 255 and g == 0 and b == 0 and a == 255,
		string.format("instance_count < 0 must not draw (got %d,%d,%d,%d)", r, g, b, a)
	)
	r, g, b, a = render(s, verts, 1, 3)
	assert(
		r == 0 and g == 255 and b == 0 and a == 255,
		string.format("instance_count = 1 must draw (got %d,%d,%d,%d)", r, g, b, a)
	)

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()
	print("INSTANCE_COUNT_ZERO_OK")
	done = true
end

return M
