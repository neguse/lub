import os


# Recursive call speed: naive double recursion
def calc(n):
    if n < 2:
        return n
    return calc(n - 1) + calc(n - 2)


def main():
    n = int(os.environ.get("BENCH_N", "30"))
    print("fib(" + str(n) + ") = " + str(calc(n)))


main()
