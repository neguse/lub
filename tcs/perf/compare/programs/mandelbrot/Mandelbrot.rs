use std::env;

// 内側のループの float の演算と分岐。mandelbrot と同じ範囲と反復 (50 回) を f32 で数える
fn run(n: i32, print: bool) -> i32 {
    let mut inside: i32 = 0;
    for y in 0..n {
        let ci = 2.0f32 * y as f32 / n as f32 - 1.0f32;
        for x in 0..n {
            let cr = 2.0f32 * x as f32 / n as f32 - 1.5f32;
            let mut zr = 0.0f32;
            let mut zi = 0.0f32;
            let mut tr = 0.0f32;
            let mut ti = 0.0f32;
            let mut i = 0;
            while i < 50 && tr + ti <= 4.0f32 {
                zi = 2.0f32 * zr * zi + ci;
                zr = tr - ti + cr;
                tr = zr * zr;
                ti = zi * zi;
                i += 1;
            }
            if tr + ti <= 4.0f32 {
                inside += 1;
            }
        }
    }
    if print {
        println!("inside: {} of {}", inside, n * n);
    }
    inside
}

fn env_i32(name: &str, def: i32) -> i32 {
    match env::var(name) {
        Ok(s) => s.parse().unwrap(),
        Err(_) => def,
    }
}

fn main() {
    let n = env_i32("BENCH_N", 200);
    // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
