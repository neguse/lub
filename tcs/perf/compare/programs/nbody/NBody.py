import math
import os


# n-body, same steps as the C# version (f64 here, f32 there)
class Body:
    __slots__ = ("x", "y", "z", "vx", "vy", "vz", "mass")

    def __init__(self, x, y, z, vx, vy, vz, mass):
        self.x = x
        self.y = y
        self.z = z
        self.vx = vx
        self.vy = vy
        self.vz = vz
        self.mass = mass


def energy(bodies):
    e = 0.0
    for i in range(len(bodies)):
        b = bodies[i]
        e = e + 0.5 * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz)
        for j in range(i + 1, len(bodies)):
            c = bodies[j]
            dx = b.x - c.x
            dy = b.y - c.y
            dz = b.z - c.z
            e = e - (b.mass * c.mass) / math.sqrt(dx * dx + dy * dy + dz * dz)
    return e


def advance(bodies, dt):
    for i in range(len(bodies)):
        b = bodies[i]
        for j in range(i + 1, len(bodies)):
            c = bodies[j]
            dx = b.x - c.x
            dy = b.y - c.y
            dz = b.z - c.z
            d2 = dx * dx + dy * dy + dz * dz
            mag = dt / (d2 * math.sqrt(d2))
            bm = c.mass * mag
            cm = b.mass * mag
            b.vx = b.vx - dx * bm
            b.vy = b.vy - dy * bm
            b.vz = b.vz - dz * bm
            c.vx = c.vx + dx * cm
            c.vy = c.vy + dy * cm
            c.vz = c.vz + dz * cm
    for b in bodies:
        b.x = b.x + dt * b.vx
        b.y = b.y + dt * b.vy
        b.z = b.z + dt * b.vz


def main():
    n = int(os.environ.get("BENCH_N", "1000"))
    pi = 3.14159265
    solar_mass = 4.0 * pi * pi
    days_per_year = 365.24

    bodies = [
        Body(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, solar_mass),
        Body(4.84143144, -1.16032004, -0.103622044,
             0.00166007664 * days_per_year, 0.00769901118 * days_per_year, -0.0000690460016 * days_per_year,
             0.000954791938 * solar_mass),
        Body(8.34336671, 4.12479856, -0.403523417,
             -0.00276742510 * days_per_year, 0.00499852801 * days_per_year, 0.0000230417297 * days_per_year,
             0.000285885980 * solar_mass),
        Body(12.8943695, -15.1111514, -0.223307579,
             0.00296460137 * days_per_year, 0.00237847173 * days_per_year, -0.0000296589568 * days_per_year,
             0.0000436624404 * solar_mass),
        Body(15.3796971, -25.9193146, 0.179258772,
             0.00268067772 * days_per_year, 0.00162824170 * days_per_year, -0.0000951592254 * days_per_year,
             0.0000515138902 * solar_mass),
    ]

    px = py = pz = 0.0
    for b in bodies:
        px = px + b.vx * b.mass
        py = py + b.vy * b.mass
        pz = pz + b.vz * b.mass
    bodies[0].vx = -px / solar_mass
    bodies[0].vy = -py / solar_mass
    bodies[0].vz = -pz / solar_mass

    print("energy before: " + str(int(energy(bodies) * 1000000.0)))
    for _ in range(n):
        advance(bodies, 0.01)
    print("energy after: " + str(int(energy(bodies) * 1000000.0)))


main()
