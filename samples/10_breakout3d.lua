local lub_io = require("lub_io")

local M = {}

local DT = 1 / 60
local STRIDE = 7 -- pos.xyz + color.rgba

local COLS = 9
local ROWS = 5
local BRICK_GAP_X = 0.035
local BRICK_GAP_Y = 0.03
local BRICK_LEFT = -0.83
local BRICK_RIGHT = 0.83
local BRICK_TOP = 0.70
local BRICK_H = 0.075
local BRICK_D = 0.16
local BRICK_W = (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS

local PADDLE_Y = -0.76
local PADDLE_W = 0.38
local PADDLE_H = 0.055
local PADDLE_D = 0.24
local PADDLE_SPEED = 1.55

local BALL_R = 0.035
local BALL_SPEED_X = 0.58
local BALL_SPEED_Y = 0.85

local row_colors = {
    {0.95, 0.24, 0.28, 1},
    {0.98, 0.55, 0.15, 1},
    {0.98, 0.86, 0.22, 1},
    {0.22, 0.70, 0.40, 1},
    {0.16, 0.58, 0.88, 1},
}

local bricks = {}
local paddle = { x = 0, prev_x = 0 }
local ball = { x = 0, y = 0, vx = BALL_SPEED_X, vy = BALL_SPEED_Y, stuck = true }
local lives = 3
local score = 0
local launch_timer = 0
local reset_was_down = false
local mesh_version = 0
local camera_t = 0

local function clamp(v, lo, hi)
    if v < lo then return lo end
    if v > hi then return hi end
    return v
end

local function is_down(name)
    return type(key_down) == "function" and key_down(name)
end

local function shade(c, k)
    return {
        clamp(c[1] * k, 0, 1),
        clamp(c[2] * k, 0, 1),
        clamp(c[3] * k, 0, 1),
        c[4] or 1,
    }
end

local function reset_bricks()
    bricks = {}
    for row = 1, ROWS do
        local y1 = BRICK_TOP - (row - 1) * (BRICK_H + BRICK_GAP_Y)
        local y0 = y1 - BRICK_H
        for col = 1, COLS do
            local x0 = BRICK_LEFT + (col - 1) * (BRICK_W + BRICK_GAP_X)
            bricks[#bricks + 1] = {
                x0 = x0,
                y0 = y0,
                x1 = x0 + BRICK_W,
                y1 = y1,
                row = row,
                alive = true,
            }
        end
    end
end

local function reset_ball()
    ball.x = paddle.x
    ball.y = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.015
    ball.vx = BALL_SPEED_X
    ball.vy = BALL_SPEED_Y
    ball.stuck = true
    launch_timer = 0
end

local function reset_game()
    paddle.x = 0
    paddle.prev_x = 0
    lives = 3
    score = 0
    reset_bricks()
    reset_ball()
end

local function launch_ball()
    if not ball.stuck then return end
    ball.stuck = false
    ball.vx = (paddle.x >= 0) and -BALL_SPEED_X or BALL_SPEED_X
    ball.vy = BALL_SPEED_Y
end

local function alive_bricks()
    local n = 0
    for _, brick in ipairs(bricks) do
        if brick.alive then n = n + 1 end
    end
    return n
end

local function circle_hits_rect(cx, cy, r, rect)
    return cx + r > rect.x0 and cx - r < rect.x1 and
           cy + r > rect.y0 and cy - r < rect.y1
end

local function bounce_from_rect(rect)
    local left = ball.x + BALL_R - rect.x0
    local right = rect.x1 - (ball.x - BALL_R)
    local bottom = ball.y + BALL_R - rect.y0
    local top = rect.y1 - (ball.y - BALL_R)
    local m = math.min(left, right, bottom, top)

    if m == left then
        ball.x = rect.x0 - BALL_R
        ball.vx = -math.abs(ball.vx)
    elseif m == right then
        ball.x = rect.x1 + BALL_R
        ball.vx = math.abs(ball.vx)
    elseif m == bottom then
        ball.y = rect.y0 - BALL_R
        ball.vy = -math.abs(ball.vy)
    else
        ball.y = rect.y1 + BALL_R
        ball.vy = math.abs(ball.vy)
    end
end

local function update_game()
    local reset_down = is_down("r")
    if reset_down and not reset_was_down then reset_game() end
    reset_was_down = reset_down

    local move = 0
    if is_down("left") or is_down("a") then move = move - 1 end
    if is_down("right") or is_down("d") then move = move + 1 end

    paddle.prev_x = paddle.x
    paddle.x = clamp(paddle.x + move * PADDLE_SPEED * DT,
                     -1 + PADDLE_W * 0.5 + 0.05,
                      1 - PADDLE_W * 0.5 - 0.05)

    if ball.stuck then
        ball.x = paddle.x
        ball.y = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.015
        launch_timer = launch_timer + DT
        if is_down("space") or launch_timer > 1.0 then launch_ball() end
        return
    end

    ball.x = ball.x + ball.vx * DT
    ball.y = ball.y + ball.vy * DT

    if ball.x - BALL_R < -0.95 then
        ball.x = -0.95 + BALL_R
        ball.vx = math.abs(ball.vx)
    elseif ball.x + BALL_R > 0.95 then
        ball.x = 0.95 - BALL_R
        ball.vx = -math.abs(ball.vx)
    end
    if ball.y + BALL_R > 0.88 then
        ball.y = 0.88 - BALL_R
        ball.vy = -math.abs(ball.vy)
    end

    local paddle_rect = {
        x0 = paddle.x - PADDLE_W * 0.5,
        y0 = PADDLE_Y - PADDLE_H * 0.5,
        x1 = paddle.x + PADDLE_W * 0.5,
        y1 = PADDLE_Y + PADDLE_H * 0.5,
    }
    if ball.vy < 0 and circle_hits_rect(ball.x, ball.y, BALL_R, paddle_rect) then
        local hit = (ball.x - paddle.x) / (PADDLE_W * 0.5)
        ball.y = paddle_rect.y1 + BALL_R
        ball.vx = clamp(hit * 0.9 + (paddle.x - paddle.prev_x) * 2.5, -0.98, 0.98)
        ball.vy = math.abs(ball.vy)
    end

    for _, brick in ipairs(bricks) do
        if brick.alive and circle_hits_rect(ball.x, ball.y, BALL_R, brick) then
            brick.alive = false
            score = score + 1
            bounce_from_rect(brick)
            break
        end
    end

    if ball.y + BALL_R < -1.0 then
        lives = lives - 1
        if lives <= 0 then reset_game() else reset_ball() end
    elseif alive_bricks() == 0 then
        reset_game()
    end
end

local function v(out, x, y, z, c)
    out[#out + 1] = x
    out[#out + 1] = y
    out[#out + 1] = z
    out[#out + 1] = c[1]
    out[#out + 1] = c[2]
    out[#out + 1] = c[3]
    out[#out + 1] = c[4]
end

local function quad(out, a, b, c, d, col)
    v(out, a[1], a[2], a[3], col)
    v(out, b[1], b[2], b[3], col)
    v(out, c[1], c[2], c[3], col)
    v(out, a[1], a[2], a[3], col)
    v(out, c[1], c[2], c[3], col)
    v(out, d[1], d[2], d[3], col)
end

local function add_box(out, cx, cy, cz, sx, sy, sz, base)
    local x0, x1 = cx - sx * 0.5, cx + sx * 0.5
    local y0, y1 = cy - sy * 0.5, cy + sy * 0.5
    local z0, z1 = cz - sz * 0.5, cz + sz * 0.5

    local p000, p100 = {x0, y0, z0}, {x1, y0, z0}
    local p010, p110 = {x0, y1, z0}, {x1, y1, z0}
    local p001, p101 = {x0, y0, z1}, {x1, y0, z1}
    local p011, p111 = {x0, y1, z1}, {x1, y1, z1}

    quad(out, p000, p100, p110, p010, shade(base, 1.05))
    quad(out, p101, p001, p011, p111, shade(base, 0.58))
    quad(out, p001, p000, p010, p011, shade(base, 0.72))
    quad(out, p100, p101, p111, p110, shade(base, 0.82))
    quad(out, p010, p110, p111, p011, shade(base, 1.22))
    quad(out, p001, p101, p100, p000, shade(base, 0.48))
end

local function add_sphere(out, cx, cy, cz, r, base)
    local rings = 8
    local segs = 16
    for ring = 0, rings - 1 do
        local v0 = -math.pi * 0.5 + ring / rings * math.pi
        local v1 = -math.pi * 0.5 + (ring + 1) / rings * math.pi
        for seg = 0, segs - 1 do
            local u0 = seg / segs * math.pi * 2
            local u1 = (seg + 1) / segs * math.pi * 2

            local function p(u, vv)
                local cv = math.cos(vv)
                return {
                    cx + math.cos(u) * cv * r,
                    cy + math.sin(vv) * r,
                    cz + math.sin(u) * cv * r,
                    math.cos(u) * cv,
                    math.sin(vv),
                    math.sin(u) * cv,
                }
            end

            local a, b = p(u0, v0), p(u1, v0)
            local c, d = p(u1, v1), p(u0, v1)
            local function col(pt)
                return shade(base, 0.70 + math.max(pt[5], 0) * 0.25 + math.max(-pt[6], 0) * 0.18)
            end
            v(out, a[1], a[2], a[3], col(a))
            v(out, b[1], b[2], b[3], col(b))
            v(out, c[1], c[2], c[3], col(c))
            v(out, a[1], a[2], a[3], col(a))
            v(out, c[1], c[2], c[3], col(c))
            v(out, d[1], d[2], d[3], col(d))
        end
    end
end

local function build_vertices()
    local out = {}
    add_box(out, 0, -0.04, 0.13, 2.05, 1.95, 0.04, {0.05, 0.07, 0.11, 1})
    add_box(out, -1.02, -0.02, -0.02, 0.05, 1.92, 0.28, {0.22, 0.27, 0.36, 1})
    add_box(out,  1.02, -0.02, -0.02, 0.05, 1.92, 0.28, {0.22, 0.27, 0.36, 1})
    add_box(out, 0, 0.93, -0.02, 2.09, 0.05, 0.28, {0.22, 0.27, 0.36, 1})

    for _, brick in ipairs(bricks) do
        if brick.alive then
            add_box(out,
                (brick.x0 + brick.x1) * 0.5,
                (brick.y0 + brick.y1) * 0.5,
                -0.03,
                brick.x1 - brick.x0,
                brick.y1 - brick.y0,
                BRICK_D,
                row_colors[brick.row])
        end
    end

    add_box(out, paddle.x, PADDLE_Y, -0.10,
            PADDLE_W, PADDLE_H, PADDLE_D, {0.94, 0.96, 0.86, 1})
    add_sphere(out, ball.x, ball.y, -0.20, BALL_R, {1.0, 0.95, 0.65, 1})

    for i = 1, lives do
        add_sphere(out, -0.88 + (i - 1) * 0.08, -0.94, -0.15,
                   0.025, {0.95, 0.32, 0.36, 1})
    end
    for i = 1, math.min(score, 12) do
        add_box(out, 0.48 + (i - 1) * 0.04, -0.94, -0.12,
                0.022, 0.055, 0.04, {0.26, 0.82, 0.62, 1})
    end

    return out
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

local function make_mvp(t)
    local yaw = -0.22 + math.sin(t * 0.35) * 0.025
    local pitch = -0.18
    local cy, sy = math.cos(yaw), math.sin(yaw)
    local cx, sx = math.cos(pitch), math.sin(pitch)
    local ry = {
        cy, 0, sy, 0,
        0, 1, 0, 0,
        -sy, 0, cy, 0,
        0, 0, 0, 1,
    }
    local rx = {
        1, 0, 0, 0,
        0, cx, -sx, 0,
        0, sx, cx, 0,
        0, 0, 0, 1,
    }
    local view = {
        1, 0, 0, 0,
        0, 1, 0, -0.02,
        0, 0, 1, 3.15,
        0, 0, 0, 1,
    }
    local f = 2.05
    local aspect = 16.0 / 9.0
    local nz, fz = 0.1, 40.0
    local proj = {
        f / aspect, 0, 0, 0,
        0, f, 0, 0,
        0, 0, fz / (fz - nz), -fz * nz / (fz - nz),
        0, 0, 1, 0,
    }
    return mul4(proj, mul4(view, mul4(rx, ry)))
end

function M.onInit()
    config({ backend = os.getenv("LUB_BACKEND") or "sokol" })
    reset_game()
end

function M.onEvent(e) end
function M.onQuit() end

function M.onFrame()
    camera_t = camera_t + DT
    update_game()

    local vs, vsv = lub_io.load_text("samples/data/10_breakout3d.vs.slang")
    local fs, fsv = lub_io.load_text("samples/data/10_breakout3d.fs.slang")
    if not vs or not fs then return end

    local verts = build_vertices()
    mesh_version = mesh_version + 1
    local shader = use_shader("breakout3d_shader", vs, fs, vsv ~ fsv)
    local vbuf = use_buffer("breakout3d_verts", VERTEX, verts, mesh_version)

    begin_pass({ target = main_tex, clear_color = {0.025, 0.032, 0.048, 1} })
        draw(math.floor(#verts / STRIDE),
             { verts = vbuf, uniforms = { mvp = make_mvp(camera_t) } },
             { shader = shader, depth = true, depth_write = true, cull = NONE })
    end_pass()
end

return M
