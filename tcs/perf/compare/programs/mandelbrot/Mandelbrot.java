// Float math and branches in the inner loop: mandelbrot range and 50 iterations in float (f32)
public class Mandelbrot {
    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 200 : Integer.parseInt(s);
        int inside = 0;
        for (int y = 0; y < n; y++) {
            float ci = 2.0f * y / n - 1.0f;
            for (int x = 0; x < n; x++) {
                float cr = 2.0f * x / n - 1.5f;
                float zr = 0.0f;
                float zi = 0.0f;
                float tr = 0.0f;
                float ti = 0.0f;
                int i = 0;
                while (i < 50 && tr + ti <= 4.0f) {
                    zi = 2.0f * zr * zi + ci;
                    zr = tr - ti + cr;
                    tr = zr * zr;
                    ti = zi * zi;
                    i++;
                }
                if (tr + ti <= 4.0f) inside++;
            }
        }
        System.out.println("inside: " + inside + " of " + n * n);
    }
}
