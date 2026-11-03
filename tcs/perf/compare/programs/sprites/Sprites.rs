use std::env;

// ゲームの典型: sprite を 1 つずつ Box で確保して Vec に持ち、毎フレーム動かして画面端で跳ね返す
struct Sprite {
    x: f32,
    y: f32,
    vx: f32,
    vy: f32,
    frame: i32,
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

fn update(s: &mut Sprite, dt: f32) {
    s.x = s.x + s.vx * dt;
    s.y = s.y + s.vy * dt;
    if s.x < 0.0f32 {
        s.x = 0.0f32;
        s.vx = -s.vx;
    }
    if s.x > 400.0f32 {
        s.x = 400.0f32;
        s.vx = -s.vx;
    }
    if s.y < 0.0f32 {
        s.y = 0.0f32;
        s.vy = -s.vy;
    }
    if s.y > 240.0f32 {
        s.y = 240.0f32;
        s.vy = -s.vy;
    }
    s.frame = (s.frame + 1) % 8;
}

fn main() {
    let frames: i32 = match env::var("BENCH_N") {
        Ok(s) => s.parse().unwrap(),
        Err(_) => 100,
    };
    let count = 1024;
    let dt = 0.02f32;
    let mut rng = Rng { state: 12345 };

    let mut sprites: Vec<Box<Sprite>> = Vec::new();
    for _ in 0..count {
        let x = rng.frand(0.0f32, 400.0f32);
        let y = rng.frand(0.0f32, 240.0f32);
        let vx = rng.frand(-60.0f32, 60.0f32);
        let vy = rng.frand(-60.0f32, 60.0f32);
        let frame = rng.lcg() % 8;
        sprites.push(Box::new(Sprite {
            x,
            y,
            vx,
            vy,
            frame,
        }));
    }

    for _ in 0..frames {
        for sp in sprites.iter_mut() {
            update(sp, dt);
        }
    }

    let mut sx = 0.0f32;
    let mut sy = 0.0f32;
    let mut sf: i32 = 0;
    for sp in sprites.iter() {
        sx = sx + sp.x;
        sy = sy + sp.y;
        sf = sf + sp.frame;
    }
    println!(
        "sum x: {}, sum y: {}, sum frame: {}",
        sx as i32, sy as i32, sf
    );
}
