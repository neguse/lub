-- n-body, same steps as the C# version
local sqrt = math.sqrt

local function newBody(x, y, z, vx, vy, vz, mass)
  return { x = x, y = y, z = z, vx = vx, vy = vy, vz = vz, mass = mass }
end

-- float to int, truncating toward zero like the C# cast
local function trunc(x)
  return x >= 0 and math.floor(x) or math.ceil(x)
end

local function energy(bodies)
  local e = 0.0
  local nb = #bodies
  for i = 1, nb do
    local b = bodies[i]
    e = e + 0.5 * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz)
    for j = i + 1, nb do
      local c = bodies[j]
      local dx = b.x - c.x
      local dy = b.y - c.y
      local dz = b.z - c.z
      e = e - (b.mass * c.mass) / sqrt(dx * dx + dy * dy + dz * dz)
    end
  end
  return e
end

local function advance(bodies, dt)
  local nb = #bodies
  for i = 1, nb do
    local b = bodies[i]
    for j = i + 1, nb do
      local c = bodies[j]
      local dx = b.x - c.x
      local dy = b.y - c.y
      local dz = b.z - c.z
      local d2 = dx * dx + dy * dy + dz * dz
      local mag = dt / (d2 * sqrt(d2))
      local bm = c.mass * mag
      local cm = b.mass * mag
      b.vx = b.vx - dx * bm
      b.vy = b.vy - dy * bm
      b.vz = b.vz - dz * bm
      c.vx = c.vx + dx * cm
      c.vy = c.vy + dy * cm
      c.vz = c.vz + dz * cm
    end
  end
  for i = 1, nb do
    local b = bodies[i]
    b.x = b.x + dt * b.vx
    b.y = b.y + dt * b.vy
    b.z = b.z + dt * b.vz
  end
end

local n = tonumber(os.getenv("BENCH_N")) or 1000
local pi = 3.14159265
local solarMass = 4.0 * pi * pi
local daysPerYear = 365.24

local bodies = {
  newBody(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, solarMass),
  newBody(4.84143144, -1.16032004, -0.103622044,
    0.00166007664 * daysPerYear, 0.00769901118 * daysPerYear, -0.0000690460016 * daysPerYear,
    0.000954791938 * solarMass),
  newBody(8.34336671, 4.12479856, -0.403523417,
    -0.00276742510 * daysPerYear, 0.00499852801 * daysPerYear, 0.0000230417297 * daysPerYear,
    0.000285885980 * solarMass),
  newBody(12.8943695, -15.1111514, -0.223307579,
    0.00296460137 * daysPerYear, 0.00237847173 * daysPerYear, -0.0000296589568 * daysPerYear,
    0.0000436624404 * solarMass),
  newBody(15.3796971, -25.9193146, 0.179258772,
    0.00268067772 * daysPerYear, 0.00162824170 * daysPerYear, -0.0000951592254 * daysPerYear,
    0.0000515138902 * solarMass),
}

local px, py, pz = 0.0, 0.0, 0.0
for i = 1, #bodies do
  px = px + bodies[i].vx * bodies[i].mass
  py = py + bodies[i].vy * bodies[i].mass
  pz = pz + bodies[i].vz * bodies[i].mass
end
bodies[1].vx = -px / solarMass
bodies[1].vy = -py / solarMass
bodies[1].vz = -pz / solarMass

print("energy before: " .. trunc(energy(bodies) * 1000000.0))
for _ = 1, n do advance(bodies, 0.01) end
print("energy after: " .. trunc(energy(bodies) * 1000000.0))
