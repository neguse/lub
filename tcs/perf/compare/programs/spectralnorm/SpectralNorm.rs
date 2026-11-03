use std::env;

// 二重ループの float の積和と int の除算。spectral-norm を f32 で計算する
fn a(i: i32, j: i32) -> f32 {
    let ij = i + j;
    1.0f32 / (ij * (ij + 1) / 2 + i + 1) as f32
}

fn multiply_av(n: i32, v: &[f32], av: &mut [f32]) {
    for i in 0..n {
        let mut sum = 0.0f32;
        for j in 0..n {
            sum = sum + a(i, j) * v[j as usize];
        }
        av[i as usize] = sum;
    }
}

fn multiply_atv(n: i32, v: &[f32], atv: &mut [f32]) {
    for i in 0..n {
        let mut sum = 0.0f32;
        for j in 0..n {
            sum = sum + a(j, i) * v[j as usize];
        }
        atv[i as usize] = sum;
    }
}

fn multiply_atav(n: i32, v: &[f32], tmp: &mut [f32], atav: &mut [f32]) {
    multiply_av(n, v, tmp);
    multiply_atv(n, tmp, atav);
}

fn run(n: i32, print: bool) -> i32 {
    let mut u = vec![0.0f32; n as usize];
    let mut v = vec![0.0f32; n as usize];
    let mut tmp = vec![0.0f32; n as usize];
    for i in 0..n as usize {
        u[i] = 1.0f32;
    }
    for _ in 0..10 {
        multiply_atav(n, &u, &mut tmp, &mut v);
        multiply_atav(n, &v, &mut tmp, &mut u);
    }
    let mut v_bv = 0.0f32;
    let mut vv = 0.0f32;
    for i in 0..n as usize {
        v_bv = v_bv + u[i] * v[i];
        vv = vv + v[i] * v[i];
    }
    let result = ((v_bv / vv).sqrt() * 1000000.0f32) as i32;
    if print {
        println!("spectral norm x 1e6: {}", result);
    }
    result
}

fn env_i32(name: &str, def: i32) -> i32 {
    match env::var(name) {
        Ok(s) => s.parse().unwrap(),
        Err(_) => def,
    }
}

fn main() {
    let n = env_i32("BENCH_N", 100);
    // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
