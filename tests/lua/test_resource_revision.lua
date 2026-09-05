-- use_* の version 規約:
--   * 省略 = 「内容が変わった」宣言。runtime が新しい実効 version を発行する
--   * ref.version の再主張 = stored と一致して upload を skip する
--   * 実効 version は entry の hot reload を跨いで巻き戻らない
local subject = require("test_resource_revision_subject")

local M = {}

function M.on_init()
	lub.config({ backend = os.getenv("LUB_BACKEND") or "sdlgpu" })
end

function M.on_frame()
	local data = { 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0, 0.0 }

	-- 変更宣言のたびに実効 version が変わる
	local b1 = lub.gfx.use_buffer("rev_test", lub.gfx.VERTEX, data)
	assert(b1.version ~= nil, "ref must carry the effective version")
	local b2 = lub.gfx.use_buffer("rev_test", lub.gfx.VERTEX, data)
	assert(b2.version ~= b1.version, "declaration must issue a fresh version")

	-- ref.version の再主張は stored と一致する (upload skip 経路)
	local b3 = lub.gfx.use_buffer("rev_test", lub.gfx.VERTEX, data, b2.version)
	assert(b3.version == b2.version, "reassertion must keep the stored version")

	-- 宣言で発行される version は hot reload を跨いで過去の値と衝突しない
	local first = subject.declare()
	local swapped, err = lume.hotswap("test_resource_revision_subject")
	assert(swapped, err)
	assert(swapped == subject, "hotswap must preserve module identity")
	local second = subject.declare()
	assert(second ~= first, "declaration after hotswap must not repeat a version")

	print(
		string.format(
			"RESOURCE_REVISION_OK declare=%d,%d reassert=%d hotswap=%d,%d",
			b1.version,
			b2.version,
			b3.version,
			first,
			second
		)
	)
	lub.quit()
end

return M
