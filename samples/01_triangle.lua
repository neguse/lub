-- samples/01_triangle.lua
local sg_io = require("sg_io")
local M = {}

function M.on_init(self)
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function M.on_event(self, e) end
function M.on_quit(self) end

function M.on_frame(self)
    local vs, vsv = sg_io.load_text("samples/data/01_triangle.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/01_triangle.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/01_triangle.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("tri_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("tri_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
    end_pass()
end

return M
