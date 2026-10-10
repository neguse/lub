-- tests/lua/test_unbound_bindings.lua
-- shader が宣言した texture / StructuredBuffer の一部か全部を bindings で
-- 渡さない draw。渡さなかった slot は backend が仮の resource で埋めるので、
-- draw は止まらず落ちずに描ける。仮の resource の値は backend ごとに違って
-- よいので、渡した resource が決める channel だけを確かめる。
-- shader: R = tex_a、G = tex_b (FS の 2 つの texture)、B = item (VS)。

local M = {}

local A, B, ITEM = 200, 100, 153
local CASES = {
	{ name = "none", textures = {}, want = {} },
	{ name = "b_only", textures = { "tex_b" }, want = { [2] = B } },
	{ name = "a_only", textures = { "tex_a" }, want = { [1] = A } },
	{ name = "no_item", textures = { "tex_a", "tex_b" }, want = { [1] = A, [2] = B } },
	{ name = "all", textures = { "tex_a", "tex_b" }, item = true, want = { [1] = A, [2] = B, [3] = ITEM } },
}

local frames = 0
local requested = false
local done = {}

local function fail(message)
	print("UNBOUND_BINDINGS_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
	for _, c in ipairs(CASES) do
		c.rb = lub.gfx.readback(c.name)
	end
end

function M.on_frame()
	frames = frames + 1
	expect(frames < 300, "readback never became ready")

	local vs, ver_vs = lub.io.load_text("tests/lua/test_unbound_bindings.vs.slang")
	local fs, ver_fs = lub.io.load_text("tests/lua/test_unbound_bindings.fs.slang")
	expect(vs and fs, "shader source missing")
	local sh = lub.gfx.use_shader("sh", vs, fs, ver_vs ~ ver_fs)
	local tex_opts = { filter = lub.gfx.NEAREST, wrap = lub.gfx.CLAMP }
	local res = {
		tex_a = lub.gfx.use_texture("tex_a", 1, 1, lub.gfx.RGBA8, { A, 0, 0, 255 }, 1, tex_opts),
		tex_b = lub.gfx.use_texture("tex_b", 1, 1, lub.gfx.RGBA8, { 0, B, 0, 255 }, 1, tex_opts),
	}
	local item = lub.gfx.use_buffer("item", lub.gfx.STORAGE, { 0, 0, ITEM / 255, 0 }, 1)

	for _, c in ipairs(CASES) do
		local rt = lub.gfx.use_texture("rt_" .. c.name, 1, 1, lub.gfx.RGBA8, nil, 1, {
			target = true,
			filter = lub.gfx.NEAREST,
			wrap = lub.gfx.CLAMP,
		})
		local bindings = {}
		for _, name in ipairs(c.textures) do
			bindings[name] = res[name]
		end
		if c.item then
			bindings.item = item
		end
		lub.gfx.begin_pass({ target = rt, clear_color = { 0, 0, 0, 1 } })
		lub.gfx.draw(3, bindings, { shader = sh, depth = false, cull = lub.gfx.NONE })
		lub.gfx.end_pass()
		c.rt = rt
	end

	lub.gfx.begin_pass({ target = lub.gfx.main_tex, clear_color = { 0, 0, 0, 1 } })
	lub.gfx.end_pass()

	for _, c in ipairs(CASES) do
		if not done[c.name] then
			local st, bytes, w, h, _, _, id, _, err
			if not requested then
				st, bytes, w, h, _, _, id, _, err = c.rb:read_texture(c.rt, 1)
			else
				st, bytes, w, h, _, _, id, _, err = c.rb:read_texture(c.rt)
			end
			expect(st ~= "error", c.name .. ": read_texture failed: " .. tostring(err))
			if st == "ready" then
				expect(id == 1 and w == 1 and h == 1, c.name .. ": unexpected readback result")
				for ch, want in pairs(c.want) do
					local got = bytes:get(ch - 1)
					expect(
						math.abs(got - want) <= 2,
						string.format("%s: channel %d got %d, want %d", c.name, ch, got, want)
					)
				end
				done[c.name] = true
			end
		end
	end
	requested = true

	for _, c in ipairs(CASES) do
		if not done[c.name] then
			return
		end
	end
	print("OK test_unbound_bindings")
	lub.app.quit()
end

return M
