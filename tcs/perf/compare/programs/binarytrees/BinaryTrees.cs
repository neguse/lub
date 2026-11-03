using System;

// 小さな object の確保と GC。The Computer Language Benchmarks Game の binary-trees (1 スレッドの版) と同じ手順
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

    public static void Main()
    {
        string s = Environment.GetEnvironmentVariable("BENCH_N");
        int n = s == null ? 10 : int.Parse(s);
        int minDepth = 4;
        int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

        int stretch = maxDepth + 1;
        Console.WriteLine("stretch tree of depth " + stretch + "\t check: " + Make(stretch).Check());

        TreeNode longLived = Make(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2)
        {
            int iterations = 1 << (maxDepth - d + minDepth);
            int check = 0;
            for (int i = 0; i < iterations; i++) check += Make(d).Check();
            Console.WriteLine(iterations + "\t trees of depth " + d + "\t check: " + check);
        }
        Console.WriteLine("long lived tree of depth " + maxDepth + "\t check: " + longLived.Check());
    }
}
