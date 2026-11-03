// Small int arrays and branches: fannkuch-redux, single-threaded
public class Fannkuch {
    static int run(int n, boolean print) {

        int[] perm = new int[n];
        int[] perm1 = new int[n];
        int[] count = new int[n];
        for (int i = 0; i < n; i++) perm1[i] = i;

        int maxFlips = 0;
        int checksum = 0;
        int permCount = 0;
        int r = n;
        while (true) {
            while (r != 1) {
                count[r - 1] = r;
                r--;
            }

            for (int i = 0; i < n; i++) perm[i] = perm1[i];
            int flips = 0;
            int k = perm[0];
            while (k != 0) {
                int lo = 0;
                int hi = k;
                while (lo < hi) {
                    int t = perm[lo];
                    perm[lo] = perm[hi];
                    perm[hi] = t;
                    lo++;
                    hi--;
                }
                flips++;
                k = perm[0];
            }
            if (flips > maxFlips) maxFlips = flips;
            if (permCount % 2 == 0) checksum += flips;
            else checksum -= flips;

            while (true) {
                if (r == n) {
                    if (print) {
                        System.out.println(checksum);
                        System.out.println("Pfannkuchen(" + n + ") = " + maxFlips);
                    }
                    return checksum;
                }
                int perm0 = perm1[0];
                for (int i = 0; i < r; i++) perm1[i] = perm1[i + 1];
                perm1[r] = perm0;
                count[r]--;
                if (count[r] > 0) break;
                r++;
            }
            permCount++;
        }
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 7 : Integer.parseInt(s);
        // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
        String rs = System.getenv("BENCH_REPS");
        int reps = rs == null ? 1 : Integer.parseInt(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}
