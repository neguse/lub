local lub_io = require("lub_io")
local M = {}

local RT_W, RT_H = 256, 256

function M.on_init()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
    local ovs, ovsv = lub_io.load_text("samples/data/05_offscreen.vs.slang")
    local ofs, ofsv = lub_io.load_text("samples/data/05_offscreen.fs.slang")
    local overts, ovv = lub_io.load_floats("samples/data/05_offscreen.verts.lua")
    local pvs, pvsv = lub_io.load_text("samples/data/05_post.vs.slang")
    local pfs, pfsv = lub_io.load_text("samples/data/05_post.fs.slang")
    local pverts, pvv = lub_io.load_floats("samples/data/05_post.verts.lua")
    if not ovs or not ofs or not overts or not pvs or not pfs or not pverts then return end

    local rt = use_texture("rt_scene", RT_W, RT_H, RGBA8, nil, 1,
                           { filter = LINEAR, wrap = CLAMP, target = true })

    local sh_off = use_shader("off_shader", ovs, ofs, ovsv ~ ofsv)
    local b_off  = use_buffer("off_verts", VERTEX, overts, ovv)
    begin_pass({ target = rt, clear_color = {0.1, 0.1, 0.2, 1} })
        draw(3, { verts = b_off },
             { shader = sh_off, depth = false, cull = NONE })
    end_pass()

    local sh_post = use_shader("post_shader", pvs, pfs, pvsv ~ pfsv)
    local b_post  = use_buffer("post_verts", VERTEX, pverts, pvv)
    begin_pass({ target = main_tex, clear_color = {0, 0, 0, 1} })
        draw(6, { verts = b_post, scene = rt },
             { shader = sh_post, depth = false, cull = NONE })
    end_pass()
end

return M
