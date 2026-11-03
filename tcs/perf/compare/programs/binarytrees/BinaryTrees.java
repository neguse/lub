// Small object allocation and GC: binary-trees, single-threaded, one object per node
class TreeNode {
    TreeNode left;
    TreeNode right;

    TreeNode(TreeNode left, TreeNode right) {
        this.left = left;
        this.right = right;
    }

    int check() {
        if (left == null) return 1;
        return 1 + left.check() + right.check();
    }
}

public class BinaryTrees {
    static TreeNode make(int depth) {
        if (depth == 0) return new TreeNode(null, null);
        return new TreeNode(make(depth - 1), make(depth - 1));
    }

    static int run(int n, boolean print) {
        int minDepth = 4;
        int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

        int stretch = maxDepth + 1;
        int stretchCheck = make(stretch).check();
        if (print) System.out.println("stretch tree of depth " + stretch + "\t check: " + stretchCheck);

        int digest = stretchCheck;
        TreeNode longLived = make(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2) {
            int iterations = 1 << (maxDepth - d + minDepth);
            int check = 0;
            for (int i = 0; i < iterations; i++) check += make(d).check();
            if (print) System.out.println(iterations + "\t trees of depth " + d + "\t check: " + check);
            digest += check;
        }
        int longCheck = longLived.check();
        if (print) System.out.println("long lived tree of depth " + maxDepth + "\t check: " + longCheck);
        return digest + longCheck;
    }

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 10 : Integer.parseInt(s);
        // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
        String rs = System.getenv("BENCH_REPS");
        int reps = rs == null ? 1 : Integer.parseInt(rs);
        int prev = 0;
        for (int r = 0; r < reps; r++) prev = run(prev == -1 ? n + 1 : n, r == reps - 1);
    }
}
