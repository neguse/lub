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

fn run(n: i32, print: bool) -> i32 {
    let min_depth = 4;
    let max_depth = if n < min_depth + 2 { min_depth + 2 } else { n };

    let stretch = max_depth + 1;
    let stretch_check = make(stretch).check();
    if print {
        println!(
            "stretch tree of depth {}\t check: {}",
            stretch, stretch_check
        );
    }
    let mut digest = stretch_check;

    let long_lived = make(max_depth);
    let mut d = min_depth;
    while d <= max_depth {
        let iterations: i32 = 1 << (max_depth - d + min_depth);
        let mut check = 0;
        for _ in 0..iterations {
            check += make(d).check();
        }
        if print {
            println!("{}\t trees of depth {}\t check: {}", iterations, d, check);
        }
        digest = digest.wrapping_add(check);
        d += 2;
    }
    let long_check = long_lived.check();
    drop(long_lived);
    if print {
        println!(
            "long lived tree of depth {}\t check: {}",
            max_depth, long_check
        );
    }
    digest.wrapping_add(long_check)
}

fn env_i32(name: &str, def: i32) -> i32 {
    match env::var(name) {
        Ok(s) => s.parse().unwrap(),
        Err(_) => def,
    }
}

fn main() {
    let n = env_i32("BENCH_N", 10);
    // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
