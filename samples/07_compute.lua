-- samples/07_compute.lua
--
-- Compute writes 3 vertices (vec4 = position.xy + color.rg) into a storage
-- buffer. The render pass then rebinds the same buffer as a VERTEX buffer
-- and draws a triangle whose vertex positions/colors came from the GPU.
local sg_io = require("sg_io")
local M = {}

function M.on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

-- 3 vertices * 4 floats (vec2 pos + vec2 col) = 12 floats.
local VERT_FLOATS = 12

function M.on_frame()
    local cs, csv = sg_io.load_text("samples/data/07_gen_verts.cs.slang")
    local vs, vsv = sg_io.load_text("samples/data/07_render.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/07_render.fs.slang")
    if not cs or not vs or not fs then return end

    local vbuf = use_buffer("compute_verts", STORAGE, VERT_FLOATS, 1)
    local sh_c = use_shader_compute("gen", cs, csv)
    local sh_r = use_shader("render", vs, fs, vsv ~ fsv)

    dispatch(1, 1, 1, { out_verts = vbuf }, { shader = sh_c })

    begin_pass({ target = main_tex, clear_color = {0.05, 0.05, 0.1, 1} })
        draw(3, { verts = vbuf },
             { shader = sh_r, depth = false, cull = NONE })
    end_pass()
end

return M
