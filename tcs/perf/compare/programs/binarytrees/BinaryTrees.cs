using System;

// Small object allocation and GC. Same procedure as binary-trees (single-thread version) of The Computer Language Benchmarks Game
public class TreeNode
{
    public TreeNode Left;
    public TreeNode Right;

    public TreeNode(TreeNode left, TreeNode right)
    {
        Left = left;
        Right = right;
    }

    public int Check()
    {
        if (Left == null) return 1;
        return 1 + Left.Check() + Right.Check();
    }
}

public class BinaryTrees
{
    static TreeNode Make(int depth)
    {
        if (depth == 0) return new TreeNode(null, null);
        return new TreeNode(Make(depth - 1), Make(depth - 1));
    }

    static int Run(int n, bool print)
    {
        int minDepth = 4;
        int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

        int stretch = maxDepth + 1;
        int stretchCheck = Make(stretch).Check();
        if (print) Console.WriteLine("stretch tree of depth " + stretch + "\t check: " + stretchCheck);

        int digest = stretchCheck;
        TreeNode longLived = Make(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2)
        {
            int iterations = 1 << (maxDepth - d + minDepth);
            int check = 0;
            for (int i = 0; i < iterations; i++) check += Make(d).Check();
            if (print) Console.WriteLine(iterations + "\t trees of depth " + d + "\t check: " + check);
            digest += check;
        }
        int longCheck = longLived.Check();
        if (print) Console.WriteLine("long lived tree of depth " + maxDepth + "\t check: " + longCheck);
        return digest + longCheck;
    }

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 10 : int.Parse(s);
        // Repeats BENCH_REPS times (run.py uses this to compute the time per repetition).
        // Each repetition's input depends on the previous result so the compiler cannot skip repetitions or hoist them out (the result is never -1)
        string rs = Environment.GetEnvironmentVariable("BENCH_REPS");
        int reps = rs == null ? 1 : int.Parse(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = Run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}
