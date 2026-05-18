local sg_io = require("sg_io")

local M = {}

local DT = 1 / 60
local STRIDE = 6 -- pos.xy + color.rgba

local COLS = 11
local ROWS = 6
local BRICK_GAP_X = 0.018
local BRICK_GAP_Y = 0.018
local BRICK_LEFT = -0.88
local BRICK_RIGHT = 0.88
local BRICK_TOP = 0.76
local BRICK_H = 0.06
local BRICK_W = (BRICK_RIGHT - BRICK_LEFT - BRICK_GAP_X * (COLS - 1)) / COLS

local PADDLE_Y = -0.78
local PADDLE_W = 0.34
local PADDLE_H = 0.045
local PADDLE_SPEED = 1.55

local BALL_R = 0.026
local BALL_SPEED_X = 0.55
local BALL_SPEED_Y = 0.83

local row_colors = {
    {0.93, 0.23, 0.25, 1.0},
    {0.96, 0.62, 0.16, 1.0},
    {0.98, 0.88, 0.24, 1.0},
    {0.22, 0.72, 0.43, 1.0},
    {0.14, 0.63, 0.86, 1.0},
    {0.55, 0.42, 0.86, 1.0},
}

local bricks = {}
local paddle = { x = 0, prev_x = 0 }
local ball = { x = 0, y = 0, vx = BALL_SPEED_X, vy = BALL_SPEED_Y, stuck = true }
local lives = 3
local score = 0
local launch_timer = 0
local reset_was_down = false
local mesh_version = 0

local function clamp(v, lo, hi)
    if v < lo then return lo end
    if v > hi then return hi end
    return v
end

local function is_down(name)
    return type(key_down) == "function" and key_down(name)
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
    ball.y = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01
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
    if reset_down and not reset_was_down then
        reset_game()
    end
    reset_was_down = reset_down

    local move = 0
    if is_down("left") or is_down("a") then move = move - 1 end
    if is_down("right") or is_down("d") then move = move + 1 end

    paddle.prev_x = paddle.x
    paddle.x = clamp(paddle.x + move * PADDLE_SPEED * DT,
                     -1 + PADDLE_W * 0.5 + 0.03,
                      1 - PADDLE_W * 0.5 - 0.03)

    if ball.stuck then
        ball.x = paddle.x
        ball.y = PADDLE_Y + PADDLE_H * 0.5 + BALL_R + 0.01
        launch_timer = launch_timer + DT
        if is_down("space") or launch_timer > 1.0 then
            launch_ball()
        end
        return
    end

    ball.x = ball.x + ball.vx * DT
    ball.y = ball.y + ball.vy * DT

    if ball.x - BALL_R < -0.96 then
        ball.x = -0.96 + BALL_R
        ball.vx = math.abs(ball.vx)
    elseif ball.x + BALL_R > 0.96 then
        ball.x = 0.96 - BALL_R
        ball.vx = -math.abs(ball.vx)
    end
    if ball.y + BALL_R > 0.90 then
        ball.y = 0.90 - BALL_R
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
        ball.vx = clamp(hit * 0.85 + (paddle.x - paddle.prev_x) * 2.5, -0.95, 0.95)
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
        if lives <= 0 then
            reset_game()
        else
            reset_ball()
        end
    elseif alive_bricks() == 0 then
        reset_game()
    end
end

local function push_vertex(out, x, y, c)
    out[#out + 1] = x
    out[#out + 1] = y
    out[#out + 1] = c[1]
    out[#out + 1] = c[2]
    out[#out + 1] = c[3]
    out[#out + 1] = c[4]
end

local function add_rect(out, x0, y0, x1, y1, c)
    push_vertex(out, x0, y0, c)
    push_vertex(out, x1, y0, c)
    push_vertex(out, x1, y1, c)
    push_vertex(out, x0, y0, c)
    push_vertex(out, x1, y1, c)
    push_vertex(out, x0, y1, c)
end

local function add_circle(out, cx, cy, r, c)
    local segments = 20
    for i = 0, segments - 1 do
        local a0 = i / segments * math.pi * 2
        local a1 = (i + 1) / segments * math.pi * 2
        push_vertex(out, cx, cy, c)
        push_vertex(out, cx + math.cos(a0) * r, cy + math.sin(a0) * r, c)
        push_vertex(out, cx + math.cos(a1) * r, cy + math.sin(a1) * r, c)
    end
end

local function build_vertices()
    local out = {}
    local rail = {0.18, 0.22, 0.30, 1}
    local paddle_color = {0.95, 0.96, 0.88, 1}
    local ball_color = {1.0, 0.98, 0.78, 1}
    local live_color = {0.92, 0.34, 0.36, 1}
    local score_color = {0.30, 0.82, 0.65, 1}

    add_rect(out, -0.99, -0.98, -0.96, 0.93, rail)
    add_rect(out,  0.96, -0.98,  0.99, 0.93, rail)
    add_rect(out, -0.99,  0.90,  0.99, 0.93, rail)

    for _, brick in ipairs(bricks) do
        if brick.alive then
            local c = row_colors[brick.row]
            add_rect(out, brick.x0, brick.y0, brick.x1, brick.y1, c)
            add_rect(out, brick.x0 + 0.006, brick.y1 - 0.012,
                          brick.x1 - 0.006, brick.y1 - 0.006,
                          {1, 1, 1, 0.20})
        end
    end

    add_rect(out, paddle.x - PADDLE_W * 0.5, PADDLE_Y - PADDLE_H * 0.5,
                  paddle.x + PADDLE_W * 0.5, PADDLE_Y + PADDLE_H * 0.5,
                  paddle_color)
    add_circle(out, ball.x, ball.y, BALL_R, ball_color)

    for i = 1, lives do
        add_circle(out, -0.86 + (i - 1) * 0.07, -0.92, 0.018, live_color)
    end
    for i = 1, math.min(score, 12) do
        local x = 0.48 + (i - 1) * 0.035
        add_rect(out, x, -0.94, x + 0.018, -0.90, score_color)
    end

    return out
end

function M.on_init()
    config({ backend = os.getenv("SGLUA_BACKEND") or "sokol" })
    reset_game()
end

function M.on_event(e) end
function M.on_quit() end

function M.on_frame()
    update_game()

    local vs, vsv = sg_io.load_text("samples/data/09_breakout.vs.slang")
    local fs, fsv = sg_io.load_text("samples/data/09_breakout.fs.slang")
    if not vs or not fs then return end

    local verts = build_vertices()
    mesh_version = mesh_version + 1
    local shader = use_shader("breakout_shader", vs, fs, vsv ~ fsv)
    local vbuf = use_buffer("breakout_verts", VERTEX, verts, mesh_version)

    begin_pass({ target = main_tex, clear_color = {0.035, 0.045, 0.065, 1} })
        draw(math.floor(#verts / STRIDE), { verts = vbuf },
             { shader = shader, depth = false, cull = NONE, blend = ALPHA })
    end_pass()
end

return M
