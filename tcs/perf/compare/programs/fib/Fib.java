// Recursive calls: naive doubly recursive fib(N)
public class Fib {
    static int calc(int n) {
        if (n < 2) return n;
        return calc(n - 1) + calc(n - 2);
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 30 : Integer.parseInt(s);
        System.out.println("fib(" + n + ") = " + calc(n));
    }
}
