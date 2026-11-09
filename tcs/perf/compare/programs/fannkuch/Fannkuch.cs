using System;

// 配列の操作と整数の分岐。The Computer Language Benchmarks Game の fannkuch-redux (1 スレッドの版) と同じ手順
public class Fannkuch
{
    static int Run(int n, bool print)
    {

        var perm = new int[n];
        var perm1 = new int[n];
        var count = new int[n];
        for (int i = 0; i < n; i++) perm1[i] = i;

        int maxFlips = 0;
        int checksum = 0;
        int permCount = 0;
        int r = n;
        while (true)
        {
            while (r != 1)
            {
                count[r - 1] = r;
                r--;
            }

            for (int i = 0; i < n; i++) perm[i] = perm1[i];
            int flips = 0;
            int k = perm[0];
            while (k != 0)
            {
                int lo = 0;
                int hi = k;
                while (lo < hi)
                {
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

            while (true)
            {
                if (r == n)
                {
                    if (print)
                    {
                        Console.WriteLine(checksum);
                        Console.WriteLine("Pfannkuchen(" + n + ") = " + maxFlips);
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

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 7 : int.Parse(s);
        // BENCH_REPS 回くり返す (run.py が 1 回あたりの時間を出すのに使う)。
        // 次の回の入力を前の回の結果に依存させ、compiler が回を省いたり外に出したりできないようにする (結果が -1 になることは無い)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}
