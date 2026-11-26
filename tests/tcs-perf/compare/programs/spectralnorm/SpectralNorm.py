import math
import os


# spectral-norm, same steps as the C# version (f64 here, f32 there)
def a(i, j):
    ij = i + j
    return 1.0 / (ij * (ij + 1) // 2 + i + 1)


def multiply_av(n, v, av):
    for i in range(n):
        s = 0.0
        for j in range(n):
            s = s + a(i, j) * v[j]
        av[i] = s


def multiply_atv(n, v, atv):
    for i in range(n):
        s = 0.0
        for j in range(n):
            s = s + a(j, i) * v[j]
        atv[i] = s


def multiply_atav(n, v, tmp, atav):
    multiply_av(n, v, tmp)
    multiply_atv(n, tmp, atav)


def run(n, show):
    u = [1.0] * n
    v = [0.0] * n
    tmp = [0.0] * n
    for _ in range(10):
        multiply_atav(n, u, tmp, v)
        multiply_atav(n, v, tmp, u)
    vbv = 0.0
    vv = 0.0
    for i in range(n):
        vbv = vbv + u[i] * v[i]
        vv = vv + v[i] * v[i]
    result = int(math.sqrt(vbv / vv) * 1000000.0)
    if show:
        print("spectral norm x 1e6: " + str(result))
    return result


def main():
    n = int(os.environ.get("BENCH_N", "100"))
    # BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
    reps = int(os.environ.get("BENCH_REPS", "1"))
    prev = 0
    for r in range(reps):
        prev = run(n + 1 if prev == -1 else n, r == reps - 1)


main()
