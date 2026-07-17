local FRAMES = 1000
local SPAWN_CAPACITY = 1024
local SPAWN_PER_FRAME = 32
local PARTICLE_COUNT = 4096
local IS_LUAJIT = type(jit) == "table"

local kernel = arg[1]
local count = tonumber(arg[2])
local variant = arg[3] or (IS_LUAJIT and "jit-off" or "interp")

local function usage(message)
  if message then
    io.stderr:write(message, "\n")
  end
  io.stderr:write(
    "usage: bench.lua {sprite_update|spawn_churn_naive|",
    "spawn_churn_pool|particles} N {interp|jit-off}\n")
  os.exit(1)
end

if variant ~= "interp" and variant ~= "jit-off" then
  usage("invalid variant: " .. tostring(variant))
end
if variant == "jit-off" and not IS_LUAJIT then
  usage("jit-off requires LuaJIT")
end
if variant == "interp" and IS_LUAJIT then
  usage("interp requires the LUA_32BITS PUC Lua interpreter")
end
if kernel == "sprite_update" then
  if count ~= 256 and count ~= 1024 and count ~= 4096 then
    usage("sprite_update N must be 256, 1024, or 4096")
  end
elseif kernel == "spawn_churn_naive" or kernel == "spawn_churn_pool" then
  if count ~= SPAWN_CAPACITY then
    usage("spawn_churn N must be 1024")
  end
elseif kernel == "particles" then
  if count ~= PARTICLE_COUNT then
    usage("particles N must be 4096")
  end
else
  usage("invalid kernel: " .. tostring(kernel))
end

local quantize = function(value)
  return value
end
local float_bytes

if IS_LUAJIT then
  local ffi = require("ffi")
  local quantize_buffer = ffi.new("float[1]")
  local digest_buffer = ffi.new("float[1]")
  local digest_bytes = ffi.cast("uint8_t *", digest_buffer)

  quantize = function(value)
    quantize_buffer[0] = value
    return tonumber(quantize_buffer[0])
  end
  float_bytes = function(value)
    digest_buffer[0] = value
    return tonumber(digest_bytes[0]), tonumber(digest_bytes[1]),
      tonumber(digest_bytes[2]), tonumber(digest_bytes[3])
  end
else
  float_bytes = function(value)
    return string.byte(string.pack("<f", value), 1, 4)
  end
end

local DT = quantize(1.0 / 50.0)
local INV_1000 = quantize(1.0 / 1000.0)
local GRAVITY = quantize(98.0)
local DAMPING = quantize(0.9)
local CENTER_PUSH = quantize(0.1)

local random_state = 12345
local lcg

if IS_LUAJIT then
  local BASE = 32768
  local MODULUS = 1073741824
  local MULTIPLIER = 29773421
  local multiplier_low = MULTIPLIER % BASE
  local multiplier_high = math.floor(MULTIPLIER / BASE)

  lcg = function()
    local state_low = random_state % BASE
    local state_high = math.floor(random_state / BASE)
    local low_product = state_low * multiplier_low
    local low = low_product % BASE
    local high = (math.floor(low_product / BASE)
      + state_low * multiplier_high
      + state_high * multiplier_low) % BASE

    random_state = (low + high * BASE + 12345) % MODULUS
    return random_state
  end
else
  local chunk = assert(load([[
    return function(state)
      return (state * 1103515245 + 12345) & 0x3fffffff
    end
  ]]))
  local lcg_step = chunk()

  lcg = function()
    random_state = lcg_step(random_state)
    return random_state
  end
end

local frand
if IS_LUAJIT then
  frand = function(lo, hi)
    local scaled = quantize((lcg() % 1000) * INV_1000)
    local span = quantize(hi - lo)
    return quantize(lo + quantize(scaled * span))
  end
else
  frand = function(lo, hi)
    return lo + (lcg() % 1000) * INV_1000 * (hi - lo)
  end
end

local fnv_init
local fnv_byte
local fnv_hex

if IS_LUAJIT then
  local bit = require("bit")
  local WORD = 65536
  local FNV_LOW = 403
  local FNV_HIGH = 256

  fnv_init = function()
    return 2166136261
  end
  fnv_byte = function(hash, byte)
    local value = bit.bxor(hash, byte)
    if value < 0 then
      value = value + 4294967296
    end

    local value_low = value % WORD
    local value_high = math.floor(value / WORD)
    local low_product = value_low * FNV_LOW
    local low = low_product % WORD
    local high = (math.floor(low_product / WORD)
      + value_low * FNV_HIGH
      + value_high * FNV_LOW) % WORD
    return low + high * WORD
  end
  fnv_hex = function(hash)
    return string.format("%04x%04x", math.floor(hash / WORD), hash % WORD)
  end
else
  local chunk = assert(load([[
    return function()
      return 0x811c9dc5
    end,
    function(hash, byte)
      return (hash ~ byte) * 16777619
    end
  ]]))
  fnv_init, fnv_byte = chunk()
  fnv_hex = function(hash)
    return string.format("%08x", hash)
  end
end

local function digest_float(hash, value)
  local byte1, byte2, byte3, byte4 = float_bytes(value)
  hash = fnv_byte(hash, byte1)
  hash = fnv_byte(hash, byte2)
  hash = fnv_byte(hash, byte3)
  hash = fnv_byte(hash, byte4)
  return hash
end

local function digest_records(records, head, position_x, position_y)
  local hash = fnv_init()
  local n = #records

  for offset = 0, n - 1 do
    local record = records[((head + offset - 1) % n) + 1]
    hash = digest_float(hash, record[position_x])
    hash = digest_float(hash, record[position_y])
    hash = digest_float(hash, record.vx)
    hash = digest_float(hash, record.vy)
  end
  return fnv_hex(hash)
end

local function run_sprite_update(n)
  local sprites = {}

  for i = 1, n do
    sprites[i] = {
      x = frand(0.0, 400.0),
      y = frand(0.0, 240.0),
      vx = frand(-60.0, 60.0),
      vy = frand(-60.0, 60.0),
      frame = lcg() % 8,
    }
  end
  collectgarbage("collect")

  local started = os.clock()
  if IS_LUAJIT then
    for _ = 1, FRAMES do
      for i = 1, n do
        local sprite = sprites[i]
        local x = quantize(sprite.x + quantize(sprite.vx * DT))
        local y = quantize(sprite.y + quantize(sprite.vy * DT))
        local vx = sprite.vx
        local vy = sprite.vy

        if x < 0.0 then
          x = 0.0
          vx = -vx
        end
        if x > 400.0 then
          x = 400.0
          vx = -vx
        end
        if y < 0.0 then
          y = 0.0
          vy = -vy
        end
        if y > 240.0 then
          y = 240.0
          vy = -vy
        end
        sprite.x = x
        sprite.y = y
        sprite.vx = vx
        sprite.vy = vy
        sprite.frame = (sprite.frame + 1) % 8
      end
    end
  else
    for _ = 1, FRAMES do
      for i = 1, n do
        local sprite = sprites[i]
        sprite.x = sprite.x + sprite.vx * DT
        sprite.y = sprite.y + sprite.vy * DT
        if sprite.x < 0.0 then
          sprite.x = 0.0
          sprite.vx = -sprite.vx
        end
        if sprite.x > 400.0 then
          sprite.x = 400.0
          sprite.vx = -sprite.vx
        end
        if sprite.y < 0.0 then
          sprite.y = 0.0
          sprite.vy = -sprite.vy
        end
        if sprite.y > 240.0 then
          sprite.y = 240.0
          sprite.vy = -sprite.vy
        end
        sprite.frame = (sprite.frame + 1) % 8
      end
    end
  end
  local elapsed = os.clock() - started
  return elapsed, digest_records(sprites, 1, "x", "y")
end

local function new_entity()
  return {
    x = frand(0.0, 400.0),
    y = frand(0.0, 240.0),
    vx = frand(-30.0, 30.0),
    vy = frand(-30.0, 30.0),
  }
end

local function reset_entity(entity)
  entity.x = frand(0.0, 400.0)
  entity.y = frand(0.0, 240.0)
  entity.vx = frand(-30.0, 30.0)
  entity.vy = frand(-30.0, 30.0)
end

local function run_spawn_churn(n, pooled)
  local ring = {}
  local head = 1

  for i = 1, n do
    ring[i] = new_entity()
  end
  collectgarbage("collect")

  local started = os.clock()
  if IS_LUAJIT then
    for _ = 1, FRAMES do
      for offset = 0, SPAWN_PER_FRAME - 1 do
        local slot = ((head + offset - 1) % n) + 1
        if pooled then
          reset_entity(ring[slot])
        else
          ring[slot] = new_entity()
        end
      end
      head = ((head + SPAWN_PER_FRAME - 1) % n) + 1

      for offset = 0, n - 1 do
        local entity = ring[((head + offset - 1) % n) + 1]
        local x = quantize(entity.x + quantize(entity.vx * DT))
        local y = quantize(entity.y + quantize(entity.vy * DT))
        local vx = entity.vx
        local vy = entity.vy

        if x < 0.0 then
          x = 0.0
          vx = -vx
        end
        if x > 400.0 then
          x = 400.0
          vx = -vx
        end
        if y < 0.0 then
          y = 0.0
          vy = -vy
        end
        if y > 240.0 then
          y = 240.0
          vy = -vy
        end
        entity.x = x
        entity.y = y
        entity.vx = vx
        entity.vy = vy
      end
    end
  else
    for _ = 1, FRAMES do
      for offset = 0, SPAWN_PER_FRAME - 1 do
        local slot = ((head + offset - 1) % n) + 1
        if pooled then
          reset_entity(ring[slot])
        else
          ring[slot] = new_entity()
        end
      end
      head = ((head + SPAWN_PER_FRAME - 1) % n) + 1

      for offset = 0, n - 1 do
        local entity = ring[((head + offset - 1) % n) + 1]
        entity.x = entity.x + entity.vx * DT
        entity.y = entity.y + entity.vy * DT
        if entity.x < 0.0 then
          entity.x = 0.0
          entity.vx = -entity.vx
        end
        if entity.x > 400.0 then
          entity.x = 400.0
          entity.vx = -entity.vx
        end
        if entity.y < 0.0 then
          entity.y = 0.0
          entity.vy = -entity.vy
        end
        if entity.y > 240.0 then
          entity.y = 240.0
          entity.vy = -entity.vy
        end
      end
    end
  end
  local elapsed = os.clock() - started
  return elapsed, digest_records(ring, head, "x", "y")
end

local function run_particles(n)
  local particles = {}

  for i = 1, n do
    particles[i] = {
      px = frand(100.0, 300.0),
      py = frand(50.0, 200.0),
      vx = frand(-40.0, 40.0),
      vy = frand(-40.0, 40.0),
    }
  end
  collectgarbage("collect")

  local started = os.clock()
  if IS_LUAJIT then
    for _ = 1, FRAMES do
      for i = 1, n do
        local particle = particles[i]
        local vy = quantize(particle.vy + quantize(GRAVITY * DT))
        local px = quantize(particle.px + quantize(particle.vx * DT))
        local py = quantize(particle.py + quantize(vy * DT))
        local vx = particle.vx

        if px < 0.0 then
          px = 0.0
          vx = quantize(-vx * DAMPING)
        end
        if px > 400.0 then
          px = 400.0
          vx = quantize(-vx * DAMPING)
        end
        if py < 0.0 then
          py = 0.0
          vy = quantize(-vy * DAMPING)
        end
        if py > 240.0 then
          py = 240.0
          vy = quantize(-vy * DAMPING)
        end

        local dx = quantize(px - 200.0)
        local dy = quantize(py - 120.0)
        local d2 = quantize(quantize(dx * dx) + quantize(dy * dy))
        if d2 < 400.0 then
          px = quantize(px + quantize(dx * CENTER_PUSH))
          py = quantize(py + quantize(dy * CENTER_PUSH))
        end

        particle.px = px
        particle.py = py
        particle.vx = vx
        particle.vy = vy
      end
    end
  else
    for _ = 1, FRAMES do
      for i = 1, n do
        local particle = particles[i]
        particle.vy = particle.vy + GRAVITY * DT
        particle.px = particle.px + particle.vx * DT
        particle.py = particle.py + particle.vy * DT
        if particle.px < 0.0 then
          particle.px = 0.0
          particle.vx = -particle.vx * DAMPING
        end
        if particle.px > 400.0 then
          particle.px = 400.0
          particle.vx = -particle.vx * DAMPING
        end
        if particle.py < 0.0 then
          particle.py = 0.0
          particle.vy = -particle.vy * DAMPING
        end
        if particle.py > 240.0 then
          particle.py = 240.0
          particle.vy = -particle.vy * DAMPING
        end

        local dx = particle.px - 200.0
        local dy = particle.py - 120.0
        local d2 = dx * dx + dy * dy
        if d2 < 400.0 then
          particle.px = particle.px + dx * CENTER_PUSH
          particle.py = particle.py + dy * CENTER_PUSH
        end
      end
    end
  end
  local elapsed = os.clock() - started
  return elapsed, digest_records(particles, 1, "px", "py")
end

local elapsed
local digest
if kernel == "sprite_update" then
  elapsed, digest = run_sprite_update(count)
elseif kernel == "spawn_churn_naive" then
  elapsed, digest = run_spawn_churn(count, false)
elseif kernel == "spawn_churn_pool" then
  elapsed, digest = run_spawn_churn(count, true)
else
  elapsed, digest = run_particles(count)
end

local ms_per_frame = elapsed * 1000.0 / FRAMES
io.write(string.format("%s,%s,%d,%.9f,%s\n",
  kernel, variant, count, ms_per_frame, digest))
