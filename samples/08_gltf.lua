-- samples/08_gltf.lua
-- glTF mesh (Box.glb) を法線可視化 shader + Y 軸回転 MVP で描く。

local lub_io = require("lub_io")
local M = {}

local t_accum = 0

local function make_mvp(t)
    -- row-major で平 float table に flatten する (Slang の
    -- SLANG_MATRIX_LAYOUT_ROW_MAJOR と整合)。
    local cs = math.cos(t)
    local sn = math.sin(t)
    -- model: Y 軸回転
    local m = {
        cs, 0, sn, 0,
        0,  1, 0,  0,
        -sn,0, cs, 0,
        0,  0, 0,  1,
    }
    -- view: translate z = +3 (D3D-style LH: camera at origin looks down +Z;
    -- move world +Z so the box sits in front of the camera)
    local v = {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 3,
        0, 0, 0, 1,
    }
    -- proj: perspective f=2.0, aspect=16/9, near=0.1, far=100
    local f = 2.0
    local aspect = 16.0 / 9.0
    local nz, fz = 0.1, 100.0
    local p = {
        f / aspect, 0, 0,                 0,
        0,          f, 0,                 0,
        0,          0, fz / (fz - nz),    -fz * nz / (fz - nz),
        0,          0, 1,                 0,
    }
    local function mul4(a, b)
        local r = {}
        for row = 0, 3 do
            for col = 0, 3 do
                local s = 0
                for k = 0, 3 do
                    s = s + a[row * 4 + k + 1] * b[k * 4 + col + 1]
                end
                r[row * 4 + col + 1] = s
            end
        end
        return r
    end
    local vm = mul4(v, m)
    return mul4(p, vm)
end

function M.on_init()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame(dt)
    t_accum = t_accum + (dt or 0.016)

    local vs, ver_vs = lub_io.load_text("samples/data/08_gltf.vs.slang")
    local fs, ver_fs = lub_io.load_text("samples/data/08_gltf.fs.slang")
    if not vs or not fs then return end
    local s = use_shader("gltf_sh", vs, fs, ver_vs ~ ver_fs)

    local mesh, mesh_ver = lub_io.load_gltf("samples/data/08_box.glb")
    if not mesh then return end

    local verts = lub_io.interleave_pn(mesh)
    local vb = use_buffer("gltf_vb", VERTEX, verts, mesh_ver)
    local ib = use_buffer("gltf_ib", INDEX, mesh.indices, mesh_ver)

    local mvp = make_mvp(t_accum)

    begin_pass({ target = main_tex, clear_color = {0.1, 0.1, 0.15, 1} })
        draw(mesh.index_count,
             { verts = vb, indices = ib, uniforms = { mvp = mvp } },
             { shader = s, depth = true, depth_write = true, cull = BACK })
    end_pass()
end

return M
