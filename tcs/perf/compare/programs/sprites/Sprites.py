import os


# Game-style: a list of sprite objects moved every frame, bouncing at the screen edges
class Sprite:
    __slots__ = ("x", "y", "vx", "vy", "frame")

    def __init__(self):
        self.x = 0.0
        self.y = 0.0
        self.vx = 0.0
        self.vy = 0.0
        self.frame = 0


state = 12345


def lcg():
    global state
    state = (state * 1103515245 + 12345) & 0x3FFFFFFF
    return state


def frand(lo, hi):
    return lo + (lcg() % 1000) * (1.0 / 1000.0) * (hi - lo)


def update(s, dt):
    s.x = s.x + s.vx * dt
    s.y = s.y + s.vy * dt
    if s.x < 0.0:
        s.x = 0.0
        s.vx = -s.vx
    if s.x > 400.0:
        s.x = 400.0
        s.vx = -s.vx
    if s.y < 0.0:
        s.y = 0.0
        s.vy = -s.vy
    if s.y > 240.0:
        s.y = 240.0
        s.vy = -s.vy
    s.frame = (s.frame + 1) % 8


def main():
    frames = int(os.environ.get("BENCH_N", "100"))
    count = 1024
    dt = 0.02

    sprites = []
    for _ in range(count):
        sp = Sprite()
        sp.x = frand(0.0, 400.0)
        sp.y = frand(0.0, 240.0)
        sp.vx = frand(-60.0, 60.0)
        sp.vy = frand(-60.0, 60.0)
        sp.frame = lcg() % 8
        sprites.append(sp)

    for _ in range(frames):
        for sp in sprites:
            update(sp, dt)

    sx = 0.0
    sy = 0.0
    sf = 0
    for sp in sprites:
        sx = sx + sp.x
        sy = sy + sp.y
        sf = sf + sp.frame
    print("sum x: " + str(int(sx)) + ", sum y: " + str(int(sy)) + ", sum frame: " + str(sf))


main()
