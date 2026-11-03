use std::env;

// ゲームの典型: struct の連続した配列の particle に重力と反射と中心への吸い込みをかける
#[derive(Clone, Copy, Default)]
struct Particle {
    px: f32,
    py: f32,
    vx: f32,
    vy: f32,
}

struct Rng {
    state: i32,
}

impl Rng {
    fn lcg(&mut self) -> i32 {
        self.state = self.state.wrapping_mul(1103515245).wrapping_add(12345) & 0x3FFFFFFF;
        self.state
    }

    fn frand(&mut self, lo: f32, hi: f32) -> f32 {
        lo + (self.lcg() % 1000) as f32 * (1.0f32 / 1000.0f32) * (hi - lo)
    }
}

fn run(frames: i32, print: bool) -> i32 {
    let n = 4096;
    let dt = 0.02f32;
    let mut rng = Rng { state: 12345 };

    let mut ps = vec![Particle::default(); n];
    for p in ps.iter_mut() {
        p.px = rng.frand(100.0f32, 300.0f32);
        p.py = rng.frand(50.0f32, 200.0f32);
        p.vx = rng.frand(-40.0f32, 40.0f32);
        p.vy = rng.frand(-40.0f32, 40.0f32);
    }

    for _ in 0..frames {
        for p in ps.iter_mut() {
            p.vy = p.vy + 98.0f32 * dt;
            p.px = p.px + p.vx * dt;
            p.py = p.py + p.vy * dt;
            if p.px < 0.0f32 {
                p.px = 0.0f32;
                p.vx = -p.vx * 0.9f32;
            }
            if p.px > 400.0f32 {
                p.px = 400.0f32;
                p.vx = -p.vx * 0.9f32;
            }
            if p.py < 0.0f32 {
                p.py = 0.0f32;
                p.vy = -p.vy * 0.9f32;
            }
            if p.py > 240.0f32 {
                p.py = 240.0f32;
                p.vy = -p.vy * 0.9f32;
            }
            let dx = p.px - 200.0f32;
            let dy = p.py - 120.0f32;
            let d2 = dx * dx + dy * dy;
            if d2 < 400.0f32 {
                p.px = p.px + dx * 0.1f32;
                p.py = p.py + dy * 0.1f32;
            }
        }
    }

    let mut sx = 0.0f32;
    let mut sy = 0.0f32;
    for p in ps.iter() {
        sx = sx + p.px;
        sy = sy + p.py;
    }
    if print {
        println!("sum x: {}, sum y: {}", sx as i32, sy as i32);
    }
    (sx as i32).wrapping_add(sy as i32)
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
