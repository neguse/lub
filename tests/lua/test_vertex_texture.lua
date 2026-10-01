-- tests/lua/test_vertex_texture.lua
-- vertex shader が LUB_TEXTURE2D を sample する (neguse/lub#68)。
-- 2x1 の texture を vertex shader で引き、左半分の quad は texel 0、右半分の
-- quad は texel 1 の色を頂点色として出す。2x1 の target を読み戻して確かめる。

local M = {}

local rb
local done = false

local TEXELS = { 255, 64, 0, 255, 0, 128, 255, 255 }

local function fail(message)
	print("VERTEX_TEXTURE_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function quad(x0, x1, u)
	-- 2 三角形 (6 頂点)。全頂点が同じ uv なので quad 内は一色になる
	return {
		x0, -1, u, 0.5, x1, -1, u, 0.5, x1, 1, u, 0.5,
		x0, -1, u, 0.5, x1, 1, u, 0.5, x0, 1, u, 0.5,
	}
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	rb = lub.gfx.readback("test")
end

function M.on_frame()
	if done then
		lub.quit()
		return
	end
	local vs, ver_vs = lub.io.load_text("tests/lua/test_vertex_texture.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_vertex_texture.fs.slang")
	expect(vs and fs, "shader source missing")
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local tex_opts = { filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	local tex = lub.gfx.use_texture("tex", 2, 1, lub.gfx.RGBA8, TEXELS, 1, tex_opts)
	local rt = lub.gfx.use_texture("rt", 2, 1, lub.gfx.RGBA8, nil, 1, { target = true, filter = lub.gfx.NEAREST })
	local verts = quad(-1, 0, 0.25)
	for _, v in ipairs(quad(0, 1, 0.75)) do
		verts[#verts + 1] = v
	end
	local vb = lub.gfx.use_buffer("vb", lub.gfx.STORAGE, verts, 1)

	lub.gfx.begin_pass({ target = rt, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.draw(12, { verts = vb, tex = tex }, { shader = sh, depth = false, cull = lub.gfx.NONE })
	lub.gfx.end_pass()

	local st, bytes, w, h, fmt, stride, id = rb:read_texture(rt, 1)
	if st ~= "ready" then
		st, bytes, w, h, fmt, stride, id = rb:read_texture(rt)
	end
	expect(st == "ready" and bytes ~= nil, "read_texture was not ready: " .. tostring(st))
	expect(id == 1 and w == 2 and h == 1 and fmt == lub.gfx.RGBA8 and stride == 8, "unexpected readback shape")
	for i = 1, 8 do
		local got = bytes:get(i - 1)
		expect(math.abs(got - TEXELS[i]) <= 1, string.format("byte %d: got %d, want %d", i, got, TEXELS[i]))
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	done = true
end

return M
