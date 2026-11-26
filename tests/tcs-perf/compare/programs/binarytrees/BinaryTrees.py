import os


# binary-trees (single-threaded); a node is a (left, right) tuple, a leaf is (None, None)
def make(depth):
    if depth == 0:
        return (None, None)
    return (make(depth - 1), make(depth - 1))


def check(node):
    left, right = node
    if left is None:
        return 1
    return 1 + check(left) + check(right)


def run(n, show):
    min_depth = 4
    max_depth = min_depth + 2 if n < min_depth + 2 else n

    stretch = max_depth + 1
    stretch_check = check(make(stretch))
    if show:
        print("stretch tree of depth " + str(stretch) + "\t check: " + str(stretch_check))

    digest = stretch_check
    long_lived = make(max_depth)
    for d in range(min_depth, max_depth + 1, 2):
        iterations = 1 << (max_depth - d + min_depth)
        chk = 0
        for _ in range(iterations):
            chk += check(make(d))
        if show:
            print(str(iterations) + "\t trees of depth " + str(d) + "\t check: " + str(chk))
        digest += chk
    long_check = check(long_lived)
    if show:
        print("long lived tree of depth " + str(max_depth) + "\t check: " + str(long_check))
    return digest + long_check


def main():
    n = int(os.environ.get("BENCH_N", "10"))
    # BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
    reps = int(os.environ.get("BENCH_REPS", "1"))
    prev = 0
    for r in range(reps):
        prev = run(n + 1 if prev == -1 else n, r == reps - 1)


main()
