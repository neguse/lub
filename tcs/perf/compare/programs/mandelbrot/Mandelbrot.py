import os


# mandelbrot: same range and 50 iterations as the C# version (f64 here, f32 there)
def main():
    n = int(os.environ.get("BENCH_N", "200"))
    inside = 0
    for y in range(n):
        ci = 2.0 * y / n - 1.0
        for x in range(n):
            cr = 2.0 * x / n - 1.5
            zr = 0.0
            zi = 0.0
            tr = 0.0
            ti = 0.0
            i = 0
            while i < 50 and tr + ti <= 4.0:
                zi = 2.0 * zr * zi + ci
                zr = tr - ti + cr
                tr = zr * zr
                ti = zi * zi
                i += 1
            if tr + ti <= 4.0:
                inside += 1
    print("inside: " + str(inside) + " of " + str(n * n))


main()
