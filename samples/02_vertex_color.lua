local sg_io = dofile("samples/sg_io.lua")

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local vs, vsv = sg_io.load_text("samples/data/02_vcol.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/02_vcol.fs.slang")
    local verts, vv = sg_io.load_floats("samples/data/02_vcol.verts.lua")
    if not vs or not fs or not verts then return end
    local s = use_shader("vc_shader", vs, fs, vsv ~ fsv)
    local b = use_buffer("vc_verts", VERTEX, verts, vv)
    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b }, { shader = s, depth = false, cull = NONE })
    end_pass()
end
