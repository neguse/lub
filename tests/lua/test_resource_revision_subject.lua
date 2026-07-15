local M = {}

-- 変更宣言 (version 省略) をして実効 version を返す
function M.declare()
	return Gfx.use_buffer("rev_subject", Gfx.VERTEX, { 0.0, 0.0, 0.0 }).version
end

return M
