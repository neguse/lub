import os


# fannkuch-redux (single-threaded), same steps as the C# version
def run(n, show):
    perm = [0] * n
    perm1 = list(range(n))
    count = [0] * n

    max_flips = 0
    checksum = 0
    perm_count = 0
    r = n
    while True:
        while r != 1:
            count[r - 1] = r
            r -= 1

        for i in range(n):
            perm[i] = perm1[i]
        flips = 0
        k = perm[0]
        while k != 0:
            lo = 0
            hi = k
            while lo < hi:
                perm[lo], perm[hi] = perm[hi], perm[lo]
                lo += 1
                hi -= 1
            flips += 1
            k = perm[0]
        if flips > max_flips:
            max_flips = flips
        if perm_count % 2 == 0:
            checksum += flips
        else:
            checksum -= flips

        while True:
            if r == n:
                if show:
                    print(checksum)
                    print("Pfannkuchen(" + str(n) + ") = " + str(max_flips))
                return checksum
            perm0 = perm1[0]
            for i in range(r):
                perm1[i] = perm1[i + 1]
            perm1[r] = perm0
            count[r] -= 1
            if count[r] > 0:
                break
            r += 1
        perm_count += 1


def main():
    n = int(os.environ.get("BENCH_N", "7"))
    # BENCH_REPS runs; each input depends on the previous result (never -1) so no run can be skipped
    reps = int(os.environ.get("BENCH_REPS", "1"))
    prev = 0
    for r in range(reps):
        prev = run(n + 1 if prev == -1 else n, r == reps - 1)


main()
