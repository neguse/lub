-- samples/08_gltf.lua
-- glTF mesh (Box.glb) を法線可視化 shader + Y 軸回転 MVP で描く。

local sg_io = require("sg_io")
local M = {}

local t_accum = 0

local function make_mvp(t)
    -- Y 軸回転 + 簡易 perspective + view (z=-3) を 4x4 行列にして
    -- column-major で平 float table に flatten する (Slang/std140 互換)。
    local cs = math.cos(t)
    local sn = math.sin(t)
    -- model: Y 軸回転 (column-major)
    local m = {
        cs, 0, -sn, 0,
        0,  1, 0,   0,
        sn, 0, cs,  0,
        0,  0, 0,   1,
    }
    -- view: translate z = -3 (column-major)
    local v = {
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, -3, 1,
    }
    -- proj: perspective f=2.0, aspect=16/9, near=0.1, far=100 (column-major)
    local f = 2.0
    local aspect = 16.0 / 9.0
    local nz, fz = 0.1, 100.0
    local p = {
        f / aspect, 0, 0, 0,
        0, f, 0, 0,
        0, 0, fz / (fz - nz), 1,
        0, 0, -fz * nz / (fz - nz), 0,
    }
    -- mvp = p * v * m (column-major).
    local function mul4(a, b)
        local r = {}
        for col = 0, 3 do
            for row = 0, 3 do
                local s = 0
                for k = 0, 3 do
                    s = s + a[row + k * 4 + 1] * b[k + col * 4 + 1]
                end
                r[row + col * 4 + 1] = s
            end
        end
        return r
    end
    local vm = mul4(v, m)
    return mul4(p, vm)
end

function M.on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame(dt)
    t_accum = t_accum + (dt or 0.016)

    local vs, ver_vs = sg_io.load_text("samples/data/08_gltf.vs.slang")
    local fs, ver_fs = sg_io.load_text("samples/data/08_gltf.fs.slang")
    if not vs or not fs then return end
    local s = use_shader("gltf_sh", vs, fs, ver_vs ~ ver_fs)

    local mesh, mesh_ver = sg_io.load_gltf("samples/data/08_box.glb")
    if not mesh then return end

    local verts = sg_io.interleave_pn(mesh)
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
