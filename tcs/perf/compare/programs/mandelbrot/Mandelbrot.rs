use std::env;

// 内側のループの float の演算と分岐。mandelbrot と同じ範囲と反復 (50 回) を f32 で数える
fn main() {
    let n: i32 = match env::var("BENCH_N") {
        Ok(s) => s.parse().unwrap(),
        Err(_) => 200,
    };
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
    println!("inside: {} of {}", inside, n * n);
}
