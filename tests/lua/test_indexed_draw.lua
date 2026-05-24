-- tests/lua/test_indexed_draw.lua
-- 4 頂点 quad を 6 index で indexed draw する最小テスト。
-- vertex 重複なしで quad を成立させられることが indexed draw の動作確認になる。

local lub_io = require("lub_io")
local M = {}

function M.onInit()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
    local vs, ver_vs = lub_io.load_text("tests/lua/test_indexed_draw.vs.slang")
    local fs, ver_fs = lub_io.load_text("tests/lua/test_indexed_draw.fs.slang")
    if not vs or not fs then return end
    local s = use_shader("sh", vs, fs, ver_vs ~ ver_fs)

    local verts = { -0.6,-0.6,  0.6,-0.6,  0.6,0.6,  -0.6,0.6 }
    local vb = use_buffer("vb", VERTEX, verts, 1)
    local indices = { 0,1,2, 0,2,3 }
    local ib = use_buffer("ib", INDEX, indices, 1)

    begin_pass({ target = main_tex, clear_color = {0.05, 0.05, 0.1, 1} })
        draw(6, { verts = vb, indices = ib },
             { shader = s, depth = false, cull = NONE })
    end_pass()
end

return M
