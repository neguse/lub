local sg_io = dofile("samples/sg_io.lua")

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local vs, vsv = sg_io.load_text("samples/data/03_tex.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/03_tex.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/03_tex.verts.lua")
    local px, w, h, fmt, pv = sg_io.load_png("samples/data/03_tex.png")
    if not vs or not fs or not verts or not px then return end
    local s = use_shader("tex_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("tex_verts", VERTEX, verts, vv)
    local t = use_texture("tex_chk", w, h, fmt, px, pv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b, diffuse = t }, { shader = s, depth = false, cull = NONE })
    end_pass()
end
