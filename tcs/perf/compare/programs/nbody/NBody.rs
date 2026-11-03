use std::env;

// float の演算と field。n-body を f32 で計算する (body は struct の固定配列)
#[derive(Clone, Copy)]
struct Body {
    x: f32,
    y: f32,
    z: f32,
    vx: f32,
    vy: f32,
    vz: f32,
    mass: f32,
}

const NB: usize = 5;

fn energy(bodies: &[Body; NB]) -> f32 {
    let mut e: f32 = 0.0;
    for i in 0..NB {
        let b = &bodies[i];
        e = e + 0.5f32 * b.mass * (b.vx * b.vx + b.vy * b.vy + b.vz * b.vz);
        for j in i + 1..NB {
            let c = &bodies[j];
            let dx = b.x - c.x;
            let dy = b.y - c.y;
            let dz = b.z - c.z;
            e = e - (b.mass * c.mass) / (dx * dx + dy * dy + dz * dz).sqrt();
        }
    }
    e
}

fn advance(bodies: &mut [Body; NB], dt: f32) {
    for i in 0..NB {
        for j in i + 1..NB {
            let dx = bodies[i].x - bodies[j].x;
            let dy = bodies[i].y - bodies[j].y;
            let dz = bodies[i].z - bodies[j].z;
            let d2 = dx * dx + dy * dy + dz * dz;
            let mag = dt / (d2 * d2.sqrt());
            let bm = bodies[j].mass * mag;
            let cm = bodies[i].mass * mag;
            bodies[i].vx = bodies[i].vx - dx * bm;
            bodies[i].vy = bodies[i].vy - dy * bm;
            bodies[i].vz = bodies[i].vz - dz * bm;
            bodies[j].vx = bodies[j].vx + dx * cm;
            bodies[j].vy = bodies[j].vy + dy * cm;
            bodies[j].vz = bodies[j].vz + dz * cm;
        }
    }
    for b in bodies.iter_mut() {
        b.x = b.x + dt * b.vx;
        b.y = b.y + dt * b.vy;
        b.z = b.z + dt * b.vz;
    }
}

fn body(x: f32, y: f32, z: f32, vx: f32, vy: f32, vz: f32, mass: f32) -> Body {
    Body {
        x,
        y,
        z,
        vx,
        vy,
        vz,
        mass,
    }
}

fn main() {
    let n: i32 = match env::var("BENCH_N") {
        Ok(s) => s.parse().unwrap(),
        Err(_) => 1000,
    };
    let pi: f32 = 3.14159265f32;
    let solar_mass: f32 = 4.0f32 * pi * pi;
    let days_per_year: f32 = 365.24f32;

    let mut bodies: [Body; NB] = [
        body(0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32, 0.0f32, solar_mass),
        body(
            4.84143144f32,
            -1.16032004f32,
            -0.103622044f32,
            0.00166007664f32 * days_per_year,
            0.00769901118f32 * days_per_year,
            -0.0000690460016f32 * days_per_year,
            0.000954791938f32 * solar_mass,
        ),
        body(
            8.34336671f32,
            4.12479856f32,
            -0.403523417f32,
            -0.00276742510f32 * days_per_year,
            0.00499852801f32 * days_per_year,
            0.0000230417297f32 * days_per_year,
            0.000285885980f32 * solar_mass,
        ),
        body(
            12.8943695f32,
            -15.1111514f32,
            -0.223307579f32,
            0.00296460137f32 * days_per_year,
            0.00237847173f32 * days_per_year,
            -0.0000296589568f32 * days_per_year,
            0.0000436624404f32 * solar_mass,
        ),
        body(
            15.3796971f32,
            -25.9193146f32,
            0.179258772f32,
            0.00268067772f32 * days_per_year,
            0.00162824170f32 * days_per_year,
            -0.0000951592254f32 * days_per_year,
            0.0000515138902f32 * solar_mass,
        ),
    ];

    let (mut px, mut py, mut pz) = (0.0f32, 0.0f32, 0.0f32);
    for b in bodies.iter() {
        px = px + b.vx * b.mass;
        py = py + b.vy * b.mass;
        pz = pz + b.vz * b.mass;
    }
    bodies[0].vx = -px / solar_mass;
    bodies[0].vy = -py / solar_mass;
    bodies[0].vz = -pz / solar_mass;

    println!("energy before: {}", (energy(&bodies) * 1000000.0f32) as i32);
    for _ in 0..n {
        advance(&mut bodies, 0.01f32);
    }
    println!("energy after: {}", (energy(&bodies) * 1000000.0f32) as i32);
}
