use std::env;

// Speed of recursive calls. Computes fib(N) with naive double recursion
fn calc(n: i32) -> i32 {
    if n < 2 {
        return n;
    }
    calc(n - 1) + calc(n - 2)
}

fn run(n: i32, print: bool) -> i32 {
    let f = calc(n);
    if print {
        println!("fib({}) = {}", n, f);
    }
    f
}

fn env_i32(name: &str, def: i32) -> i32 {
    match env::var(name) {
        Ok(s) => s.parse().unwrap(),
        Err(_) => def,
    }
}

fn main() {
    let n = env_i32("BENCH_N", 30);
    // Repeats BENCH_REPS times. Each repetition's input depends on the previous result so the compiler cannot skip repetitions (the result is never -1)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
