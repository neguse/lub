-- use_* / snd の「同じ version なら data は読まない」規約。
-- version が一致する宣言では data を変換せず、nil でも (配列として不正な値でも)
-- 通る。version が一致しない (または省略した) ときだけ data が要る。
-- 不正な値 ("not an array") は読まれた時点で error になるので、読まれたかどうかの
-- 検出に使う。
local M = {}

local BAD = "not an array"

local function fail(message)
	print("DEFERRED_DATA_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function expect_error(what, fn, pattern)
	local ok, err = pcall(fn)
	expect(not ok, what .. ": expected an error")
	expect(tostring(err):find(pattern), what .. ": unexpected message: " .. tostring(err))
end

function M.on_init()
	lub.app.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

local function buffers()
	local data = { 1.0, 2.0, 3.0, 4.0 }
	local b1 = lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, data, 1)
	expect(b1 and b1.version == 1, "use_buffer: first declaration")

	local b2 = lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, BAD, 1)
	expect(b2 and b2.handle == b1.handle and b2.version == 1, "use_buffer: matching version must not read data")
	local b3 = lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, nil, 1)
	expect(b3 and b3.handle == b1.handle, "use_buffer: matching version must accept nil data")

	-- 一致しない version では data を読み、不正なら error
	expect_error("use_buffer bad data on mismatch", function()
		lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, BAD, 2)
	end, "must be a table")
	expect_error("use_buffer nil data on mismatch", function()
		lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, nil, 2)
	end, "data")
	expect_error("use_buffer nil data, first declaration", function()
		lub.gfx.use_buffer("dd_buf_new", lub.gfx.STORAGE, nil, 1)
	end, "data")
	-- version を省くと常に「変わった」宣言なので data を読む
	expect_error("use_buffer bad data without version", function()
		lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, BAD)
	end, "must be a table")

	local b4 = lub.gfx.use_buffer("dd_buf", lub.gfx.STORAGE, data, 2)
	expect(b4 and b4.version == 2 and b4.handle == b1.handle, "use_buffer: version bump with data")
end

local function buffers_ints()
	local b1 = lub.gfx.use_buffer_ints("dd_ints", lub.gfx.INDEX, { 0, 1, 2 }, 1)
	expect(b1 and b1.version == 1, "use_buffer_ints: first declaration")
	local b2 = lub.gfx.use_buffer_ints("dd_ints", lub.gfx.INDEX, BAD, 1)
	expect(b2 and b2.handle == b1.handle, "use_buffer_ints: matching version must not read data")
	local b3 = lub.gfx.use_buffer_ints("dd_ints", lub.gfx.INDEX, nil, 1)
	expect(b3 and b3.handle == b1.handle, "use_buffer_ints: matching version must accept nil data")
	expect_error("use_buffer_ints bad data on mismatch", function()
		lub.gfx.use_buffer_ints("dd_ints", lub.gfx.INDEX, BAD, 2)
	end, "must be a table")
	expect_error("use_buffer_ints nil data on mismatch", function()
		lub.gfx.use_buffer_ints("dd_ints", lub.gfx.INDEX, nil, 2)
	end, "data")
end

local function textures()
	local px = { 255, 0, 0, 255 }
	local t1 = lub.gfx.use_texture("dd_tex", 1, 1, lub.gfx.RGBA8, px, 1)
	expect(t1 and t1.version == 1, "use_texture: first declaration")
	local t2 = lub.gfx.use_texture("dd_tex", 1, 1, lub.gfx.RGBA8, BAD, 1)
	expect(t2 and t2.handle == t1.handle, "use_texture: matching version must not read px")
	local t3 = lub.gfx.use_texture("dd_tex", 1, 1, lub.gfx.RGBA8, nil, 1)
	expect(t3 and t3.handle == t1.handle, "use_texture: matching version must accept nil px")

	expect_error("use_texture bad px on mismatch", function()
		lub.gfx.use_texture("dd_tex", 1, 1, lub.gfx.RGBA8, BAD, 2)
	end, "must be a table")
	-- opts が変わると version が同じでも作り直すので px の読みに進む
	expect_error("use_texture bad px with changed opts", function()
		lub.gfx.use_texture("dd_tex", 1, 1, lub.gfx.RGBA8, BAD, 1, { filter = lub.gfx.NEAREST })
	end, "must be a table")
	-- px nil は空 texture の宣言として mismatch でも通る (render target 等)
	local rt = lub.gfx.use_texture("dd_rt", 1, 1, lub.gfx.RGBA8, nil, 1, { target = true })
	expect(rt and rt.version == 1, "use_texture: empty target texture")
end

local function sounds()
	local pcm = {}
	for i = 1, 64 do
		pcm[i] = math.sin(i * 0.1) * 0.5
	end
	local s1 = lub.audio.snd("dd_snd", pcm, 1, 48000, 1)
	expect(s1 ~= 0, "snd: first declaration")
	expect(lub.audio.snd("dd_snd", BAD, 1, 48000, 1) == s1, "snd: matching version must not read data")
	expect(lub.audio.snd("dd_snd", nil, 1, 48000, 1) == s1, "snd: matching version must accept nil data")
	expect_error("snd bad data on mismatch", function()
		lub.audio.snd("dd_snd", BAD, 1, 48000, 2)
	end, "must be a table")
	expect_error("snd nil data on mismatch", function()
		lub.audio.snd("dd_snd", nil, 1, 48000, 2)
	end, "data")
	expect_error("snd nil data, first declaration", function()
		lub.audio.snd("dd_snd_new", nil, 1, 48000, 1)
	end, "data")

	-- snd_bytes: f32 PCM の bytes
	local bytes = string.pack("<ffff", 0.1, 0.2, 0.3, 0.4)
	local b1 = lub.audio.snd_bytes("dd_bytes", bytes, 1, 48000, 1)
	expect(b1 ~= 0, "snd_bytes: first declaration")
	expect(lub.audio.snd_bytes("dd_bytes", nil, 1, 48000, 1) == b1, "snd_bytes: matching version must accept nil data")
	-- 元のファイルの bytes (4 の倍数でない長さ) を渡しても、一致する間は読まれない
	expect(
		lub.audio.snd_bytes("dd_bytes", "RIFFx", 1, 48000, 1) == b1,
		"snd_bytes: matching version must not check the length of data"
	)
	expect_error("snd_bytes nil data on mismatch", function()
		lub.audio.snd_bytes("dd_bytes", nil, 1, 48000, 2)
	end, "data")
	expect_error("snd_bytes misaligned data on mismatch", function()
		lub.audio.snd_bytes("dd_bytes", "RIFFx", 1, 48000, 2)
	end, "f32%-aligned")
	expect(lub.audio.snd_bytes("dd_bytes", bytes, 1, 48000, 2) ~= 0, "snd_bytes: version bump with data")
end

function M.on_frame()
	buffers()
	buffers_ints()
	textures()
	sounds()
	print("DEFERRED_DATA_OK")
	lub.app.quit()
end

return M
