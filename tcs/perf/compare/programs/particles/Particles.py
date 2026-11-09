import os


# Game-style: particles with gravity, wall bounce and a pull toward the centre
class Particle:
    __slots__ = ("px", "py", "vx", "vy")

    def __init__(self):
        self.px = 0.0
        self.py = 0.0
        self.vx = 0.0
        self.vy = 0.0


state = 12345


def lcg():
    global state
    state = (state * 1103515245 + 12345) & 0x3FFFFFFF
    return state


def frand(lo, hi):
    return lo + (lcg() % 1000) * (1.0 / 1000.0) * (hi - lo)


def run(frames, show):
    global state
    state = 12345
    n = 4096
    dt = 0.02

    ps = [Particle() for _ in range(n)]
    for p in ps:
        p.px = frand(100.0, 300.0)
        p.py = frand(50.0, 200.0)
        p.vx = frand(-40.0, 40.0)
        p.vy = frand(-40.0, 40.0)

    for _ in range(frames):
        for p in ps:
            p.vy = p.vy + 98.0 * dt
            p.px = p.px + p.vx * dt
            p.py = p.py + p.vy * dt
            if p.px < 0.0:
                p.px = 0.0
                p.vx = -p.vx * 0.9
            if p.px > 400.0:
                p.px = 400.0
                p.vx = -p.vx * 0.9
            if p.py < 0.0:
                p.py = 0.0
                p.vy = -p.vy * 0.9
            if p.py > 240.0:
                p.py = 240.0
                p.vy = -p.vy * 0.9
            dx = p.px - 200.0
            dy = p.py - 120.0
            d2 = dx * dx + dy * dy
            if d2 < 400.0:
                p.px = p.px + dx * 0.1
                p.py = p.py + dy * 0.1

    sx = 0.0
    sy = 0.0
    for p in ps:
        sx = sx + p.px
        sy = sy + p.py
    if show:
        print("sum x: " + str(int(sx)) + ", sum y: " + str(int(sy)))
    return int(sx) + int(sy)


def main():
    n = int(os.environ.get("BENCH_N", "100"))
    # BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
    reps = int(os.environ.get("BENCH_REPS", "1"))
    prev = 0
    for r in range(reps):
        prev = run(n + 1 if prev == -1 else n, r == reps - 1)


main()
