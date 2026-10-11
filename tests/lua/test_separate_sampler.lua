-- tests/lua/test_separate_sampler.lua
-- texture と sampler を別に宣言する shader。
-- 1. `Texture2D left; SamplerState left_sampler; ...` が LUB_TEXTURE2D と同じ絵に
--    なる。left は REPEAT、right は CLAMP の 2x1 texture で、各 texture が
--    自分の sampler で引けていれば 2x1 の target は (texel 0 of left, texel 1
--    of right) になる。
-- 2. 1 つの sampler を 2 つの texture で共有する shader と、組になる texture の
--    無い sampler を持つ shader は、描画で落ちるのではなく shader compile の
--    error になり、error は LUB_TEXTURE2D を案内する。

local M = {}

local LEFT = { 255, 0, 0, 255, 0, 255, 0, 255 }
local RIGHT = { 0, 0, 255, 255, 255, 255, 0, 255 }
local WANT = { 255, 0, 0, 255, 255, 255, 0, 255 }

local cases = {
	{ name = "separate", fs = "tests/lua/test_separate_sampler.fs.slang" },
	{ name = "macro", fs = "tests/lua/test_separate_sampler_macro.fs.slang" },
}
local frames = 0
local done = false

local function fail(message)
	print("SEPARATE_SAMPLER_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function expect_compile_error(vs, path, needles)
	local fs = lub.io.load_text(path)
	expect(fs, "shader source missing: " .. path)
	local ok, err = pcall(lub.gfx.use_shader, path, vs, fs, 1)
	expect(not ok, path .. ": shader compile must fail")
	for _, needle in ipairs(needles) do
		expect(string.find(err, needle, 1, true), path .. ": error must mention " .. needle .. ": " .. tostring(err))
	end
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	for _, c in ipairs(cases) do
		c.rb = lub.gfx.readback(c.name)
	end
end

function M.on_frame()
	if done then
		lub.app.quit()
		return
	end
	frames = frames + 1
	expect(frames < 300, "read_texture never became ready")
	local vs, ver_vs = lub.io.load_text("tests/lua/test_separate_sampler.vs.slang")
	expect(vs, "shader source missing")
	local left = lub.gfx.use_texture("left", 2, 1, lub.gfx.RGBA8, LEFT, 1, { filter = lub.gfx.NEAREST, wrap = lub.gfx.REPEAT })
	local right = lub.gfx.use_texture("right", 2, 1, lub.gfx.RGBA8, RIGHT, 1, { filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP })

	local pending = 0
	for _, c in ipairs(cases) do
		local fs, ver_fs = lub.io.load_text(c.fs)
		expect(fs, "shader source missing: " .. c.fs)
		local sh = lub.gfx.use_shader(c.name, vs, fs, ver_vs * 31 + ver_fs)
		local rt = lub.gfx.use_texture(c.name .. "_rt", 2, 1, lub.gfx.RGBA8, nil, 1, { target = true, filter = lub.gfx.NEAREST })
		lub.gfx.begin_pass({ target = rt, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
		lub.gfx.draw(3, { left = left, right = right }, { shader = sh, depth = false, cull = lub.gfx.NONE })
		lub.gfx.end_pass()

		if not c.bytes then
			local st, bytes, w, h, fmt, stride, _, _, err = c.rb:read_texture(rt, frames == 1 and 1 or nil)
			expect(st ~= "error", c.name .. ": read_texture failed: " .. tostring(err))
			if st == "ready" then
				expect(w == 2 and h == 1 and fmt == lub.gfx.RGBA8 and stride == 8, c.name .. ": unexpected readback shape")
				c.bytes = bytes
			else
				pending = pending + 1
			end
		end
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0.0, 0.0, 0.0, 1.0 } })
	lub.gfx.end_pass()
	if pending > 0 then
		return
	end

	for _, c in ipairs(cases) do
		for i = 1, 8 do
			local got = c.bytes:get(i - 1)
			expect(
				math.abs(got - WANT[i]) <= 1,
				string.format("%s: byte %d: got %d, want %d", c.name, i, got, WANT[i])
			)
		end
	end

	expect_compile_error(vs, "tests/lua/test_separate_sampler_shared.fs.slang", { "second", "shared", "LUB_TEXTURE2D" })
	expect_compile_error(vs, "tests/lua/test_separate_sampler_extra.fs.slang", { "extra", "LUB_TEXTURE2D" })
	done = true
end

return M
