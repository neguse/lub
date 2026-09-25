-- use_buffer / use_buffer_ints / use_texture / audio.snd の data は、key が
-- すでに同じ version を持っていれば読まない。
--   * 同じ version の宣言は data を読まずに今の resource を返す
--   * version が違えば data を読んで upload する
--   * data = nil は再主張だけ: 持っていれば今の resource、無ければ nil, "not found"
--   * texture は大きさ・形式・opts が変われば同じ version でも作り直す
local M = {}

local function fail(message)
	print("LAZY_DATA_FAIL " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local function expect_error(pattern, fn, ...)
	local ok, err = pcall(fn, ...)
	expect(not ok, "expected an error matching '" .. pattern .. "'")
	expect(
		string.find(tostring(err), pattern, 1, true) ~= nil,
		"error '" .. tostring(err) .. "' does not mention '" .. pattern .. "'"
	)
end

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 64, height = 64 })
end

-- 同じ version の宣言が data の大きさに比例する仕事をしないこと。data を
-- 読んでいれば 50 回の hit は 1 回の upload (読み + 転送) より何十倍も重い。
local function check_hit_skips_data()
	local n = 2000000
	local big = {}
	for i = 1, n do
		big[i] = (i % 97) * 0.01
	end
	local t0 = os.clock()
	local first = lub.gfx.use_buffer("lz_big", lub.gfx.STORAGE, big, 11)
	local t_upload = os.clock() - t0
	expect(first ~= nil and first.version == 11, "first declaration must upload")
	local t1 = os.clock()
	local again
	for _ = 1, 50 do
		again = lub.gfx.use_buffer("lz_big", lub.gfx.STORAGE, big, 11)
	end
	local t_hits = os.clock() - t1
	expect(again.handle == first.handle and again.version == 11, "hit must return the stored buffer")
	expect(
		t_hits < t_upload,
		string.format("50 hits took %.3f ms, one upload %.3f ms: the hit reads data", t_hits * 1000, t_upload * 1000)
	)
	return t_upload, t_hits
end

local function check_buffers()
	local data = { 0.0, 1.0, 2.0, 3.0 }
	local b1 = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, data, 1)
	expect(b1 ~= nil and b1.version == 1, "use_buffer must declare version 1")

	-- 同じ version: 同じ buffer
	local b2 = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, { 9.0, 9.0, 9.0, 9.0 }, 1)
	expect(b2.handle == b1.handle and b2.version == 1, "same version must return the stored buffer")

	-- version が違えば upload する
	local b3 = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, { 4.0, 5.0, 6.0 }, 2)
	expect(b3.handle == b1.handle and b3.version == 2, "a new version must upload into the same key")

	-- data = nil は再主張だけ
	local b4 = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, nil, 2)
	expect(b4 ~= nil and b4.handle == b1.handle and b4.version == 2, "nil data with the stored version must hit")
	local miss, why = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, nil, 3)
	expect(miss == nil and why == "not found", "nil data with another version must return nil, 'not found'")
	local absent, why2 = lub.gfx.use_buffer("lz_absent", lub.gfx.STORAGE, nil, 1)
	expect(absent == nil and why2 == "not found", "nil data for an unknown key must return nil, 'not found'")
	expect(lub.gfx.lookup_buffer("lz_absent") == nil, "a missed reassertion must not create an entry")
	-- 外れた再主張は何も変えない
	local b5 = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, nil, 2)
	expect(b5 ~= nil and b5.version == 2, "a missed reassertion must keep the stored version")

	-- 種別が違えば同じ version でも作り直す (data を読む)
	local idx = lub.gfx.use_buffer("lz_buf", lub.gfx.INDEX, { 0, 1, 2 }, 2)
	expect(idx.handle == b1.handle and idx.version == 2, "type change keeps the key")
	local stale_type = lub.gfx.use_buffer("lz_buf", lub.gfx.STORAGE, nil, 2)
	expect(stale_type == nil, "reassertion with another type must miss")

	-- 空の data は従来どおり error
	expect_error("empty data", lub.gfx.use_buffer, "lz_buf", lub.gfx.STORAGE, {}, 2)
	expect_error("empty data", lub.gfx.use_buffer_ints, "lz_ints", lub.gfx.STORAGE, {}, 2)

	-- 整数列
	local i1 = lub.gfx.use_buffer_ints("lz_ints", lub.gfx.INDEX, { 0, 1, 2 }, 7)
	local i2 = lub.gfx.use_buffer_ints("lz_ints", lub.gfx.INDEX, { 5, 5, 5 }, 7)
	expect(i2.handle == i1.handle and i2.version == 7, "use_buffer_ints same version must hit")
	local i3 = lub.gfx.use_buffer_ints("lz_ints", lub.gfx.INDEX, nil, 7)
	expect(i3 ~= nil and i3.handle == i1.handle, "use_buffer_ints nil data must reassert")
	expect(lub.gfx.use_buffer_ints("lz_ints", lub.gfx.INDEX, nil, 8) == nil, "use_buffer_ints nil data must miss")
end

local function px(w, h, v)
	local t = {}
	for i = 1, w * h * 4 do
		t[i] = v
	end
	return t
end

local function check_textures()
	local t1 = lub.gfx.use_texture("lz_tex", 2, 2, lub.gfx.RGBA8, px(2, 2, 255), 1)
	local t2 = lub.gfx.use_texture("lz_tex", 2, 2, lub.gfx.RGBA8, px(2, 2, 0), 1)
	expect(t2.handle == t1.handle and t2.version == 1, "same texture version must hit")

	-- 同じ version でも render target にすれば作り直す
	local rt = lub.gfx.use_texture("lz_tex", 2, 2, lub.gfx.RGBA8, nil, 1, { target = true })
	lub.gfx.begin_pass({ target = rt })
	lub.gfx.end_pass()
	-- 画素付き (問い合わせを通る) で target でなくすと、また作り直す
	local plain = lub.gfx.use_texture("lz_tex", 2, 2, lub.gfx.RGBA8, px(2, 2, 7), 1)
	expect(plain.handle == rt.handle, "opts change keeps the key")
	expect_error("target=true", lub.gfx.begin_pass, { target = plain })

	-- 同じ version でも大きさが変われば作り直す
	local a = lub.gfx.use_texture("lz_rt_a", 4, 4, lub.gfx.RGBA8, nil, 1, { target = true })
	local b = lub.gfx.use_texture("lz_rt_b", 8, 8, lub.gfx.RGBA8, nil, 1, { target = true })
	expect_error("same size", lub.gfx.begin_pass, { targets = { a, b } })
	a = lub.gfx.use_texture("lz_rt_a", 8, 8, lub.gfx.RGBA8, nil, 1, { target = true })
	lub.gfx.begin_pass({ targets = { a, b } })
	lub.gfx.end_pass()

	-- 画素を持つ texture の大きさ違いも作り直す (問い合わせが外れて画素を読む)
	local s1 = lub.gfx.use_texture("lz_size", 2, 2, lub.gfx.RGBA8, px(2, 2, 1), 3)
	expect_error("byte size mismatch", lub.gfx.use_texture, "lz_size", 4, 4, lub.gfx.RGBA8, px(2, 2, 1), 3)
	local s2 = lub.gfx.use_texture("lz_size", 4, 4, lub.gfx.RGBA8, px(4, 4, 1), 3)
	expect(s2.handle == s1.handle and s2.version == 3, "size change under the same version must recreate")
end

local function check_snd()
	local samples = {}
	for i = 1, 480 do
		samples[i] = math.sin(i * 0.1) * 0.25
	end
	local s1 = lub.audio.snd("lz_snd", samples, 1, 48000, 3)
	expect(s1 ~= nil and s1 ~= 0, "snd must be declared")
	local s2 = lub.audio.snd("lz_snd", nil, 1, 48000, 3)
	expect(s2 == s1, "snd nil data with the stored version must reassert")
	local s3, why = lub.audio.snd("lz_snd", nil, 1, 48000, 4)
	expect(s3 == nil and why == "not found", "snd nil data with another version must return nil, 'not found'")
	local s4 = lub.audio.snd("lz_snd", nil, 1, 48000, 3)
	expect(s4 == s1, "a missed snd reassertion must keep the stored snd")
end

function M.on_frame()
	local ok, err = pcall(function()
		check_buffers()
		check_textures()
		check_snd()
		local t_upload, t_hits = check_hit_skips_data()
		print(string.format("LAZY_DATA_OK upload=%.3fms hits50=%.3fms", t_upload * 1000, t_hits * 1000))
	end)
	if not ok then
		fail(tostring(err))
	end
	os.exit(0, true)
end

return M
