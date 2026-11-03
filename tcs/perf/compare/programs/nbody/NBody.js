"use strict";
// Float math and object fields: n-body in float (f32), every result rounded with Math.fround
class Body {
  constructor(x, y, z, vx, vy, vz, mass) {
    this.x = x;
    this.y = y;
    this.z = z;
    this.vx = vx;
    this.vy = vy;
    this.vz = vz;
    this.mass = mass;
  }
}

const F0_5 = Math.fround(0.5);
const F1E6 = Math.fround(1000000.0);

function energy(bodies) {
  let e = 0.0;
  for (let i = 0; i < bodies.length; i++) {
    const b = bodies[i];
    const vv = Math.fround(
      Math.fround(Math.fround(Math.fround(b.vx * b.vx) + Math.fround(b.vy * b.vy)) + Math.fround(b.vz * b.vz)),
    );
    e = Math.fround(e + Math.fround(Math.fround(F0_5 * b.mass) * vv));
    for (let j = i + 1; j < bodies.length; j++) {
      const c = bodies[j];
      const dx = Math.fround(b.x - c.x);
      const dy = Math.fround(b.y - c.y);
      const dz = Math.fround(b.z - c.z);
      const d2 = Math.fround(Math.fround(Math.fround(dx * dx) + Math.fround(dy * dy)) + Math.fround(dz * dz));
      e = Math.fround(e - Math.fround(Math.fround(b.mass * c.mass) / Math.fround(Math.sqrt(d2))));
    }
  }
  return e;
}

function advance(bodies, dt) {
  for (let i = 0; i < bodies.length; i++) {
    const b = bodies[i];
    for (let j = i + 1; j < bodies.length; j++) {
      const c = bodies[j];
      const dx = Math.fround(b.x - c.x);
      const dy = Math.fround(b.y - c.y);
      const dz = Math.fround(b.z - c.z);
      const d2 = Math.fround(Math.fround(Math.fround(dx * dx) + Math.fround(dy * dy)) + Math.fround(dz * dz));
      const mag = Math.fround(dt / Math.fround(d2 * Math.fround(Math.sqrt(d2))));
      const bm = Math.fround(c.mass * mag);
      const cm = Math.fround(b.mass * mag);
      b.vx = Math.fround(b.vx - Math.fround(dx * bm));
      b.vy = Math.fround(b.vy - Math.fround(dy * bm));
      b.vz = Math.fround(b.vz - Math.fround(dz * bm));
      c.vx = Math.fround(c.vx + Math.fround(dx * cm));
      c.vy = Math.fround(c.vy + Math.fround(dy * cm));
      c.vz = Math.fround(c.vz + Math.fround(dz * cm));
    }
  }
  for (let i = 0; i < bodies.length; i++) {
    const b = bodies[i];
    b.x = Math.fround(b.x + Math.fround(dt * b.vx));
    b.y = Math.fround(b.y + Math.fround(dt * b.vy));
    b.z = Math.fround(b.z + Math.fround(dt * b.vz));
  }
}

function run(n, print) {
  const f = Math.fround;
  const pi = f(3.14159265);
  const solarMass = f(f(f(4.0) * pi) * pi);
  const daysPerYear = f(365.24);

  const bodies = new Array(5);
  bodies[0] = new Body(0.0, 0.0, 0.0, 0.0, 0.0, 0.0, solarMass);
  bodies[1] = new Body(f(4.84143144), f(-1.16032004), f(-0.103622044),
    f(f(0.00166007664) * daysPerYear), f(f(0.00769901118) * daysPerYear), f(f(-0.0000690460016) * daysPerYear),
    f(f(0.000954791938) * solarMass));
  bodies[2] = new Body(f(8.34336671), f(4.12479856), f(-0.403523417),
    f(f(-0.0027674251) * daysPerYear), f(f(0.00499852801) * daysPerYear), f(f(0.0000230417297) * daysPerYear),
    f(f(0.00028588598) * solarMass));
  bodies[3] = new Body(f(12.8943695), f(-15.1111514), f(-0.223307579),
    f(f(0.00296460137) * daysPerYear), f(f(0.00237847173) * daysPerYear), f(f(-0.0000296589568) * daysPerYear),
    f(f(0.0000436624404) * solarMass));
  bodies[4] = new Body(f(15.3796971), f(-25.9193146), f(0.179258772),
    f(f(0.00268067772) * daysPerYear), f(f(0.0016282417) * daysPerYear), f(f(-0.0000951592254) * daysPerYear),
    f(f(0.0000515138902) * solarMass));

  let px = 0.0, py = 0.0, pz = 0.0;
  for (let i = 0; i < bodies.length; i++) {
    px = f(px + f(bodies[i].vx * bodies[i].mass));
    py = f(py + f(bodies[i].vy * bodies[i].mass));
    pz = f(pz + f(bodies[i].vz * bodies[i].mass));
  }
  bodies[0].vx = f(-px / solarMass);
  bodies[0].vy = f(-py / solarMass);
  bodies[0].vz = f(-pz / solarMass);

  const dt = f(0.01);
  const before = Math.trunc(f(energy(bodies) * F1E6));
  for (let i = 0; i < n; i++) advance(bodies, dt);
  const after = Math.trunc(f(energy(bodies) * F1E6));
  if (print) {
    console.log("energy before: " + before);
    console.log("energy after: " + after);
  }
  return after;
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 1000 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
