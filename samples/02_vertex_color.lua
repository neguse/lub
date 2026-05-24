local lub_io = require("lub_io")
local M = {}

function M.onInit()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
    local vs, vsv = lub_io.load_text("samples/data/02_vcol.vs.slang")
    local fs, fsv = lub_io.load_text("samples/data/02_vcol.fs.slang")
    local verts, vv = lub_io.load_floats("samples/data/02_vcol.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("vc_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("vc_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
    end_pass()
end

return M
