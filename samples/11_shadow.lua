-- samples/11_shadow.lua
-- Shadow mapping: render light-space depth into an offscreen float target,
-- using a depth attachment to keep the closest caster.

local lub_io = require("lub_io")
local M = {}

local STRIDE = 10 -- pos.xyz + normal.xyz + color.rgba
local SHADOW_SIZE = 1024
local DT = 1 / 60

local t_accum = 0
local mesh_version = 0

local function v(out, x, y, z, nx, ny, nz, c)
    out[#out + 1] = x
    out[#out + 1] = y
    out[#out + 1] = z
    out[#out + 1] = nx
    out[#out + 1] = ny
    out[#out + 1] = nz
    out[#out + 1] = c[1]
    out[#out + 1] = c[2]
    out[#out + 1] = c[3]
    out[#out + 1] = c[4]
end

local function tri(out, a, b, c, n, col)
    v(out, a[1], a[2], a[3], n[1], n[2], n[3], col)
    v(out, b[1], b[2], b[3], n[1], n[2], n[3], col)
    v(out, c[1], c[2], c[3], n[1], n[2], n[3], col)
end

local function quad(out, a, b, c, d, n, col)
    tri(out, a, b, c, n, col)
    tri(out, a, c, d, n, col)
end

local function add_floor(out)
    local n = {0, 1, 0}
    quad(out, {-2.3, 0, -1.55}, { 2.3, 0, -1.55},
              { 2.3, 0,  1.75}, {-2.3, 0,  1.75}, n,
              {0.50, 0.55, 0.50, 1})

    local line = {0.38, 0.42, 0.39, 1}
    for i = -4, 4 do
        local x = i * 0.48
        quad(out, {x - 0.005, 0.003, -1.55}, {x + 0.005, 0.003, -1.55},
                  {x + 0.005, 0.003,  1.75}, {x - 0.005, 0.003,  1.75}, n, line)
    end
    for i = -3, 3 do
        local z = i * 0.48
        quad(out, {-2.3, 0.003, z - 0.005}, {2.3, 0.003, z - 0.005},
                  { 2.3, 0.003, z + 0.005}, {-2.3, 0.003, z + 0.005}, n, line)
    end
end

local function add_box(out, cx, cy, cz, sx, sy, sz, col)
    local x0, x1 = cx - sx * 0.5, cx + sx * 0.5
    local y0, y1 = cy - sy * 0.5, cy + sy * 0.5
    local z0, z1 = cz - sz * 0.5, cz + sz * 0.5

    local p000, p100 = {x0, y0, z0}, {x1, y0, z0}
    local p010, p110 = {x0, y1, z0}, {x1, y1, z0}
    local p001, p101 = {x0, y0, z1}, {x1, y0, z1}
    local p011, p111 = {x0, y1, z1}, {x1, y1, z1}

    quad(out, p000, p010, p110, p100, { 0,  0, -1}, col)
    quad(out, p001, p101, p111, p011, { 0,  0,  1}, col)
    quad(out, p000, p001, p011, p010, {-1,  0,  0}, col)
    quad(out, p100, p110, p111, p101, { 1,  0,  0}, col)
    quad(out, p010, p011, p111, p110, { 0,  1,  0}, col)
    quad(out, p000, p100, p101, p001, { 0, -1,  0}, col)
end

local function add_sphere(out, cx, cy, cz, r, col)
    local rings = 12
    local segs = 24
    for ring = 0, rings - 1 do
        local v0 = -math.pi * 0.5 + ring / rings * math.pi
        local v1 = -math.pi * 0.5 + (ring + 1) / rings * math.pi
        for seg = 0, segs - 1 do
            local u0 = seg / segs * math.pi * 2
            local u1 = (seg + 1) / segs * math.pi * 2
            local function p(u, vv)
                local cv = math.cos(vv)
                local nx = math.cos(u) * cv
                local ny = math.sin(vv)
                local nz = math.sin(u) * cv
                return {cx + nx * r, cy + ny * r, cz + nz * r, nx, ny, nz}
            end
            local a, b = p(u0, v0), p(u1, v0)
            local c, d = p(u1, v1), p(u0, v1)
            v(out, a[1], a[2], a[3], a[4], a[5], a[6], col)
            v(out, b[1], b[2], b[3], b[4], b[5], b[6], col)
            v(out, c[1], c[2], c[3], c[4], c[5], c[6], col)
            v(out, a[1], a[2], a[3], a[4], a[5], a[6], col)
            v(out, c[1], c[2], c[3], c[4], c[5], c[6], col)
            v(out, d[1], d[2], d[3], d[4], d[5], d[6], col)
        end
    end
end

local function add_casters(out, t)
    add_box(out, -0.05, 0.12, 0.48, 0.88, 0.24, 0.34, {0.95, 0.76, 0.38, 1})
    add_box(out, -0.58, 0.52 + math.sin(t * 1.4) * 0.07, -0.12,
            0.42, 0.42, 0.42, {0.18, 0.72, 0.78, 1})
    add_sphere(out,
        0.62 + math.cos(t * 1.1) * 0.20,
        0.58 + math.sin(t * 1.7) * 0.08,
       -0.18 + math.sin(t * 0.8) * 0.22,
        0.22, {0.95, 0.28, 0.34, 1})
    add_box(out, 0.92, 0.34, 0.36, 0.18, 0.68, 0.18, {0.48, 0.39, 0.86, 1})
end

local function build_meshes(t)
    local casters = {}
    local scene = {}
    add_floor(scene)
    add_casters(casters, t)
    for _, f in ipairs(casters) do
        scene[#scene + 1] = f
    end
    return casters, scene
end

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

local function dot(a, b)
    return a[1] * b[1] + a[2] * b[2] + a[3] * b[3]
end

local function cross(a, b)
    return {
        a[2] * b[3] - a[3] * b[2],
        a[3] * b[1] - a[1] * b[3],
        a[1] * b[2] - a[2] * b[1],
    }
end

local function norm(vv)
    local len = math.sqrt(dot(vv, vv))
    return {vv[1] / len, vv[2] / len, vv[3] / len}
end

local function sub(a, b)
    return {a[1] - b[1], a[2] - b[2], a[3] - b[3]}
end

local function look_at_lh(eye, target, up)
    local z = norm(sub(target, eye))
    local x = norm(cross(up, z))
    local y = cross(z, x)
    return {
        x[1], x[2], x[3], -dot(x, eye),
        y[1], y[2], y[3], -dot(y, eye),
        z[1], z[2], z[3], -dot(z, eye),
        0,    0,    0,     1,
    }
end

local function perspective_lh(fov_deg, aspect, nz, fz)
    local f = 1 / math.tan(fov_deg * math.pi / 360)
    return {
        f / aspect, 0, 0,              0,
        0,          f, 0,              0,
        0,          0, fz / (fz - nz), -fz * nz / (fz - nz),
        0,          0, 1,              0,
    }
end

local function ortho_lh(w, h, nz, fz)
    return {
        2 / w, 0,     0,              0,
        0,     2 / h, 0,              0,
        0,     0,     1 / (fz - nz), -nz / (fz - nz),
        0,     0,     0,              1,
    }
end

local function camera_mvp(t)
    local eye = {2.0 + math.sin(t * 0.25) * 0.12, 1.35, -2.85}
    local view = look_at_lh(eye, {0.05, 0.34, 0.12}, {0, 1, 0})
    return mul4(perspective_lh(52, 16.0 / 9.0, 0.1, 40.0), view)
end

local function light_mvp()
    local light_pos = {-2.0, 3.3, -1.5}
    local view = look_at_lh(light_pos, {0.08, 0.24, 0.08}, {0, 1, 0})
    return mul4(ortho_lh(3.4, 3.4, 0.1, 7.0), view)
end

function M.on_init()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame(dt)
    t_accum = t_accum + (dt or DT)

    local dvs, dvsv = lub_io.load_text("samples/data/11_shadow_depth.vs.slang")
    local dfs, dfsv = lub_io.load_text("samples/data/11_shadow_depth.fs.slang")
    local svs, svsv = lub_io.load_text("samples/data/11_shadow_scene.vs.slang")
    local sfs, sfsv = lub_io.load_text("samples/data/11_shadow_scene.fs.slang")
    if not dvs or not dfs or not svs or not sfs then return end

    local depth_shader = use_shader("shadow_depth_shader", dvs, dfs, dvsv ~ dfsv)
    local scene_shader = use_shader("shadow_scene_shader", svs, sfs, svsv ~ sfsv)

    local shadow_map = use_texture("shadow_map", SHADOW_SIZE, SHADOW_SIZE,
        RGBA8, nil, 1, { target = true, filter = NEAREST, wrap = CLAMP })
    local shadow_depth = use_texture("shadow_depth", SHADOW_SIZE, SHADOW_SIZE,
        DEPTH16, nil, 1, { target = true, filter = NEAREST, wrap = CLAMP })

    local casters, scene = build_meshes(t_accum)
    mesh_version = mesh_version + 1
    local caster_buf = use_buffer("shadow_casters", VERTEX, casters, mesh_version)
    local scene_buf = use_buffer("shadow_scene", VERTEX, scene, mesh_version)

    local lmvp = light_mvp()
    begin_pass({
        target = shadow_map,
        depth_target = shadow_depth,
        clear_color = {1, 1, 1, 1},
        clear_depth = 1,
    })
        draw(math.floor(#casters / STRIDE),
             { verts = caster_buf, uniforms = { light_mvp = lmvp } },
             { shader = depth_shader, depth = true, depth_write = true, cull = NONE })
    end_pass()

    begin_pass({ target = main_tex, clear_color = {0.09, 0.12, 0.15, 1} })
        draw(math.floor(#scene / STRIDE),
             {
                verts = scene_buf,
                shadow_map = shadow_map,
                uniforms = {
                    mvp = camera_mvp(t_accum),
                    light_mvp = lmvp,
                },
             },
             { shader = scene_shader, depth = true, depth_write = true, cull = NONE })
    end_pass()
end

return M
