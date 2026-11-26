// Recursive calls: naive doubly recursive fib(N)
public class Fib {
    static int calc(int n) {
        if (n < 2) return n;
        return calc(n - 1) + calc(n - 2);
    }

    static int run(int n, boolean print) {
        int f = calc(n);
        if (print) System.out.println("fib(" + n + ") = " + f);
        return f;
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 30 : Integer.parseInt(s);
        // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
        String rs = System.getenv("BENCH_REPS");
        int reps = rs == null ? 1 : Integer.parseInt(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}
