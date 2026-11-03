use std::env;

// 再帰呼び出しの速さ。fib(N) を素朴な二重再帰で計算する
fn calc(n: i32) -> i32 {
    if n < 2 {
        return n;
    }
    calc(n - 1) + calc(n - 2)
}

fn main() {
    let n: i32 = match env::var("BENCH_N") {
        Ok(s) => s.parse().unwrap(),
        Err(_) => 30,
    };
    println!("fib({}) = {}", n, calc(n));
}
