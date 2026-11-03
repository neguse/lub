"use strict";
// Game loop: particle array with gravity, bounces and a pull toward the centre
// JS has no value types, so the C# struct array becomes an array of plain objects created up front
let state = 12345;

function lcg() {
  state = (Math.imul(state, 1103515245) + 12345) & 0x3fffffff;
  return state;
}

const STEP = Math.fround(1.0 / 1000.0);
const F0_9 = Math.fround(0.9);
const F0_1 = Math.fround(0.1);

function frand(lo, hi) {
  return Math.fround(lo + Math.fround(Math.fround(Math.fround(lcg() % 1000) * STEP) * Math.fround(hi - lo)));
}

function run(frames, print) {
  state = 12345;
  const n = 4096;
  const dt = Math.fround(0.02);

  const ps = new Array(n);
  for (let i = 0; i < n; i++) ps[i] = { px: 0.0, py: 0.0, vx: 0.0, vy: 0.0 };
  for (let i = 0; i < n; i++) {
    ps[i].px = frand(100.0, 300.0);
    ps[i].py = frand(50.0, 200.0);
    ps[i].vx = frand(-40.0, 40.0);
    ps[i].vy = frand(-40.0, 40.0);
  }

  for (let f = 0; f < frames; f++) {
    for (let i = 0; i < n; i++) {
      ps[i].vy = Math.fround(ps[i].vy + Math.fround(98.0 * dt));
      ps[i].px = Math.fround(ps[i].px + Math.fround(ps[i].vx * dt));
      ps[i].py = Math.fround(ps[i].py + Math.fround(ps[i].vy * dt));
      if (ps[i].px < 0.0) { ps[i].px = 0.0; ps[i].vx = Math.fround(-ps[i].vx * F0_9); }
      if (ps[i].px > 400.0) { ps[i].px = 400.0; ps[i].vx = Math.fround(-ps[i].vx * F0_9); }
      if (ps[i].py < 0.0) { ps[i].py = 0.0; ps[i].vy = Math.fround(-ps[i].vy * F0_9); }
      if (ps[i].py > 240.0) { ps[i].py = 240.0; ps[i].vy = Math.fround(-ps[i].vy * F0_9); }
      const dx = Math.fround(ps[i].px - 200.0);
      const dy = Math.fround(ps[i].py - 120.0);
      const d2 = Math.fround(Math.fround(dx * dx) + Math.fround(dy * dy));
      if (d2 < 400.0) {
        ps[i].px = Math.fround(ps[i].px + Math.fround(dx * F0_1));
        ps[i].py = Math.fround(ps[i].py + Math.fround(dy * F0_1));
      }
    }
  }

  let sx = 0.0;
  let sy = 0.0;
  for (let i = 0; i < n; i++) {
    sx = Math.fround(sx + ps[i].px);
    sy = Math.fround(sy + ps[i].py);
  }
  if (print) console.log("sum x: " + Math.trunc(sx) + ", sum y: " + Math.trunc(sy));
  return (Math.trunc(sx) + Math.trunc(sy)) | 0;
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 100 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
