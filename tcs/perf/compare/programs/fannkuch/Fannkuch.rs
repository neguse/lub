use std::env;

// 配列の操作と整数の分岐。fannkuch-redux (1 スレッドの版) と同じ手順
fn run(n: i32, print: bool) -> i32 {
    let mut perm = vec![0i32; n as usize];
    let mut perm1 = vec![0i32; n as usize];
    let mut count = vec![0i32; n as usize];
    for i in 0..n {
        perm1[i as usize] = i;
    }

    let mut max_flips: i32 = 0;
    let mut checksum: i32 = 0;
    let mut perm_count: i32 = 0;
    let mut r: i32 = n;
    loop {
        while r != 1 {
            count[(r - 1) as usize] = r;
            r -= 1;
        }

        for i in 0..n as usize {
            perm[i] = perm1[i];
        }
        let mut flips: i32 = 0;
        let mut k = perm[0];
        while k != 0 {
            let mut lo: i32 = 0;
            let mut hi: i32 = k;
            while lo < hi {
                let t = perm[lo as usize];
                perm[lo as usize] = perm[hi as usize];
                perm[hi as usize] = t;
                lo += 1;
                hi -= 1;
            }
            flips += 1;
            k = perm[0];
        }
        if flips > max_flips {
            max_flips = flips;
        }
        if perm_count % 2 == 0 {
            checksum += flips;
        } else {
            checksum -= flips;
        }

        loop {
            if r == n {
                if print {
                    println!("{}", checksum);
                    println!("Pfannkuchen({}) = {}", n, max_flips);
                }
                return checksum;
            }
            let perm0 = perm1[0];
            for i in 0..r as usize {
                perm1[i] = perm1[i + 1];
            }
            perm1[r as usize] = perm0;
            count[r as usize] -= 1;
            if count[r as usize] > 0 {
                break;
            }
            r += 1;
        }
        perm_count += 1;
    }
}

fn env_i32(name: &str, def: i32) -> i32 {
    match env::var(name) {
        Ok(s) => s.parse().unwrap(),
        Err(_) => def,
    }
}

fn main() {
    let n = env_i32("BENCH_N", 7);
    // BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
    let reps = env_i32("BENCH_REPS", 1);
    let mut prev: i32 = 0;
    for r in 0..reps {
        prev = run(if prev == -1 { n + 1 } else { n }, r == reps - 1);
    }
}
