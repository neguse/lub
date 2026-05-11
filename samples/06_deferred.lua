local sg_io = dofile("samples/sg_io.lua")

local RT_W, RT_H = 256, 256

function on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end
function on_event(e) end
function on_quit() end

function on_frame()
    local gvs, gvsv = sg_io.load_text("samples/data/06_gbuffer.vs.slang")
    local gfs, gfsv = sg_io.load_text("samples/data/06_gbuffer.fs.slang")
    local gverts, gvv = sg_io.load_floats("samples/data/06_gbuffer.verts.lua")
    local vvs, vvsv = sg_io.load_text("samples/data/06_view.vs.slang")
    local vfs, vfsv = sg_io.load_text("samples/data/06_view.fs.slang")
    local vverts, vvv = sg_io.load_floats("samples/data/06_view.verts.lua")
    if not gvs or not gfs or not gverts or not vvs or not vfs or not vverts then return end

    -- G-buffer attachments. Two render-target textures of the same size.
    local gbuf0 = use_texture("gbuf0", RT_W, RT_H, RGBA8, nil, 1,
                              { filter = LINEAR, wrap = CLAMP, target = true })
    local gbuf1 = use_texture("gbuf1", RT_W, RT_H, RGBA8, nil, 1,
                              { filter = LINEAR, wrap = CLAMP, target = true })

    -- G-buffer pass: MRT write. SV_Target0 -> gbuf0, SV_Target1 -> gbuf1.
    local sh_g = use_shader("gbuf_shader", gvs, gfs, gvsv ~ gfsv)
    local b_g  = use_buffer("gbuf_verts", VERTEX, gverts, gvv)
    begin_pass({
        targets = { gbuf0, gbuf1 },
        clear_colors = { {0.1, 0.1, 0.15, 1}, {0.15, 0.1, 0.1, 1} },
    })
        draw(3, { verts = b_g },
             { shader = sh_g, depth = false, cull = NONE })
    end_pass()

    -- View pass: split-screen visualization. Left half samples gbuf0,
    -- right half samples gbuf1. Same shader; only the texture binding and
    -- the (scale, offset) transform uniform differ.
    local sh_v = use_shader("view_shader", vvs, vfs, vvsv ~ vfsv)
    local b_v  = use_buffer("view_verts", VERTEX, vverts, vvv)
    begin_pass({ target = main_tex, clear_color = {0, 0, 0, 1} })
        draw(6, { verts = b_v, gbuf = gbuf0,
                  uniforms = { transform = { 0.5, 1.0, -0.5, 0.0 } } },
             { shader = sh_v, depth = false, cull = NONE })
        draw(6, { verts = b_v, gbuf = gbuf1,
                  uniforms = { transform = { 0.5, 1.0,  0.5, 0.0 } } },
             { shader = sh_v, depth = false, cull = NONE })
    end_pass()
end
