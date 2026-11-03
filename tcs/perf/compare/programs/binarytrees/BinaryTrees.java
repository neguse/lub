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

    public static void main(String[] args) {
        String s = System.getenv("BENCH_N");
        int n = s == null ? 10 : Integer.parseInt(s);
        int minDepth = 4;
        int maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

        int stretch = maxDepth + 1;
        System.out.println("stretch tree of depth " + stretch + "\t check: " + make(stretch).check());

        TreeNode longLived = make(maxDepth);
        for (int d = minDepth; d <= maxDepth; d += 2) {
            int iterations = 1 << (maxDepth - d + minDepth);
            int check = 0;
            for (int i = 0; i < iterations; i++) check += make(d).check();
            System.out.println(iterations + "\t trees of depth " + d + "\t check: " + check);
        }
        System.out.println("long lived tree of depth " + maxDepth + "\t check: " + longLived.check());
    }
}
