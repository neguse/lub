import os


# Recursive call speed: naive double recursion
def calc(n):
    if n < 2:
        return n
    return calc(n - 1) + calc(n - 2)


def run(n, show):
    f = calc(n)
    if show:
        print("fib(" + str(n) + ") = " + str(f))
    return f


def main():
    n = int(os.environ.get("BENCH_N", "30"))
    # BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
    reps = int(os.environ.get("BENCH_REPS", "1"))
    prev = 0
    for r in range(reps):
        prev = run(n + 1 if prev == -1 else n, r == reps - 1)


main()
