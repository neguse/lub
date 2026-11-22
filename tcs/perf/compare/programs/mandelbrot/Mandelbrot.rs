use std::env;

// Float arithmetic and branches in the inner loop. Counts over the same range and iterations (50) as mandelbrot, in f32
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
    // Repeats BENCH_REPS times. Each repetition's input depends on the previous result so the compiler cannot skip repetitions (the result is never -1)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
