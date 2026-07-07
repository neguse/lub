-- font core API (font_metrics / font_glyph / font_glyph_mesh / font_kern) の
-- 純関数テスト。ラスタも三角形化も CPU 決定的なので、値の性質だけでなく
-- 呼び出し間の一致も見る。
local M = {}

local FONT_PATH = "samples/21_iroha/data/MPLUS1p-subset.ttf"

local function fail(message)
	print("FONT_SMOKE_FAIL: " .. message)
	os.exit(1, true)
end

local function expect(cond, message)
	if not cond then
		fail(message)
	end
end

local frame = 0
local ttf

function M.onInit()
	config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu", width = 320, height = 180 })
end

local function cp(s)
	return (utf8.codepoint(s))
end

local function run_checks()
	local fh = io.open(FONT_PATH, "rb")
	expect(fh ~= nil, "font file missing: " .. FONT_PATH)
	ttf = fh:read("a")
	fh:close()
	expect(#ttf > 0, "font file empty")

	-- 壊れたデータはエラー (missing glyph の nil とは区別される)
	expect(pcall(font_metrics, "not a font") == false, "bogus font must error")

	local m = font_metrics(ttf)
	expect(m.ascent > 0, "ascent must be positive: " .. tostring(m.ascent))
	expect(m.descent < 0, "descent must be negative: " .. tostring(m.descent))

	-- bitmap glyph: 「い」 32px
	local gi = font_glyph(ttf, cp("い"), 32)
	expect(gi ~= nil, "glyph い missing")
	expect(gi.w > 0 and gi.h > 0, "glyph い empty bitmap")
	expect(gi.bytes ~= nil and #gi.bytes == gi.w * gi.h, "glyph い bytes size mismatch")
	expect(gi.advance > 0, "glyph い advance")
	-- 決定性: 同じ入力は同じ形
	local gi2 = font_glyph(ttf, cp("い"), 32)
	expect(
		gi2.w == gi.w and gi2.h == gi.h and gi2.xoff == gi.xoff and gi2.yoff == gi.yoff,
		"glyph い not deterministic"
	)
	expect(gi2.bytes == gi.bytes, "glyph い pixels not deterministic")

	-- 空白 glyph: bitmap 無しでも advance は返る
	local sp = font_glyph(ttf, 0x20, 32)
	expect(sp ~= nil and sp.bytes == nil and sp.advance > 0, "space glyph contract")

	-- カバレッジ外 (アラビア文字) は nil → fallback は呼び出し側の責務
	expect(font_glyph(ttf, 0x0626, 32) == nil, "uncovered codepoint must be nil")

	-- mesh glyph: 「ほ」 (穴・複数輪郭持ち)
	local gm = font_glyph_mesh(ttf, cp("ほ"))
	expect(gm ~= nil, "mesh ほ missing")
	expect(gm.vert_count > 0 and gm.index_count > 0, "mesh ほ empty")
	expect(gm.index_count % 3 == 0, "mesh ほ index count not triangles")
	for i = 1, gm.index_count do
		local idx = gm.indices[i]
		expect(idx >= 0 and idx < gm.vert_count, "mesh ほ index out of range: " .. tostring(idx))
	end
	for i = 1, gm.vert_count do
		local x = gm.positions[(i - 1) * 3 + 1]
		local y = gm.positions[(i - 1) * 3 + 2]
		local z = gm.positions[(i - 1) * 3 + 3]
		expect(z == 0, "mesh ほ z must be 0")
		expect(x > -1 and x < 2 and y > -1 and y < 2, "mesh ほ position out of em range")
	end
	expect(gm.advance > 0, "mesh ほ advance")

	-- tolerance を細かくすると頂点は減らない
	local fine = font_glyph_mesh(ttf, cp("ほ"), 0.0005)
	expect(fine.vert_count >= gm.vert_count, "finer tolerance must not reduce verts")

	-- 空白 glyph の mesh は空だが advance を持つ
	local spm = font_glyph_mesh(ttf, 0x20)
	expect(spm ~= nil and spm.vert_count == 0 and spm.index_count == 0 and spm.advance > 0, "space mesh contract")

	-- kern は数値 (M+ の kana に有意なペアは無くてよい)
	expect(type(font_kern(ttf, cp("A"), cp("V"))) == "number", "kern must return number")

	-- 統合: glyph bitmap (string) を table に blit して R8 テクスチャに上げ、
	-- mesh を頂点バッファに流す (実際の atlas 経路と同じ形)
	local px = { string.byte(gi.bytes, 1, gi.w * gi.h) }
	local nonzero = 0
	for i = 1, #px do
		if px[i] > 0 then
			nonzero = nonzero + 1
		end
	end
	expect(nonzero > 0, "glyph い bitmap has no coverage")
	local tex = use_texture("font_glyph_i", gi.w, gi.h, R8, px, 1)
	expect(tex ~= nil, "use_texture with glyph pixels failed")
	local verts = {}
	for i = 1, gm.vert_count * 3 do
		verts[i] = gm.positions[i]
	end
	local buf = use_buffer("font_mesh_ho", VERTEX, verts, 1)
	expect(buf ~= nil, "use_buffer with glyph mesh failed")
end

function M.onFrame()
	frame = frame + 1
	begin_pass({ target = main_tex, clear_color = { 0.02, 0.03, 0.04, 1.0 } })
	end_pass()
	if frame == 1 then
		run_checks()
		print("FONT_SMOKE_OK")
		quit()
	end
end

return M
