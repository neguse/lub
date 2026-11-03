use std::env;

// 小さな object の確保。node は 1 つずつ Box で確保する (arena は使わない)
struct TreeNode {
    left: Option<Box<TreeNode>>,
    right: Option<Box<TreeNode>>,
}

impl TreeNode {
    fn check(&self) -> i32 {
        match (&self.left, &self.right) {
            (Some(l), Some(r)) => 1 + l.check() + r.check(),
            _ => 1,
        }
    }
}

fn make(depth: i32) -> Box<TreeNode> {
    if depth == 0 {
        return Box::new(TreeNode {
            left: None,
            right: None,
        });
    }
    Box::new(TreeNode {
        left: Some(make(depth - 1)),
        right: Some(make(depth - 1)),
    })
}

fn main() {
    let n: i32 = match env::var("BENCH_N") {
        Ok(s) => s.parse().unwrap(),
        Err(_) => 10,
    };
    let min_depth = 4;
    let max_depth = if n < min_depth + 2 { min_depth + 2 } else { n };

    let stretch = max_depth + 1;
    println!(
        "stretch tree of depth {}\t check: {}",
        stretch,
        make(stretch).check()
    );

    let long_lived = make(max_depth);
    let mut d = min_depth;
    while d <= max_depth {
        let iterations: i32 = 1 << (max_depth - d + min_depth);
        let mut check = 0;
        for _ in 0..iterations {
            check += make(d).check();
        }
        println!("{}\t trees of depth {}\t check: {}", iterations, d, check);
        d += 2;
    }
    println!(
        "long lived tree of depth {}\t check: {}",
        max_depth,
        long_lived.check()
    );
}
