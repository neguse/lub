"use strict";
// Game loop: class sprites in an array, moved every frame and bounced at the screen edges
class Sprite {
  constructor() {
    this.x = 0.0;
    this.y = 0.0;
    this.vx = 0.0;
    this.vy = 0.0;
    this.frame = 0;
  }
}

let state = 12345;

function lcg() {
  state = (Math.imul(state, 1103515245) + 12345) & 0x3fffffff;
  return state;
}

const STEP = Math.fround(1.0 / 1000.0);

function frand(lo, hi) {
  return Math.fround(lo + Math.fround(Math.fround(Math.fround(lcg() % 1000) * STEP) * Math.fround(hi - lo)));
}

function update(s, dt) {
  s.x = Math.fround(s.x + Math.fround(s.vx * dt));
  s.y = Math.fround(s.y + Math.fround(s.vy * dt));
  if (s.x < 0.0) { s.x = 0.0; s.vx = -s.vx; }
  if (s.x > 400.0) { s.x = 400.0; s.vx = -s.vx; }
  if (s.y < 0.0) { s.y = 0.0; s.vy = -s.vy; }
  if (s.y > 240.0) { s.y = 240.0; s.vy = -s.vy; }
  s.frame = ((s.frame + 1) | 0) % 8;
}

function run(frames, print) {
  state = 12345;
  const count = 1024;
  const dt = Math.fround(0.02);

  const sprites = [];
  for (let i = 0; i < count; i++) {
    const sp = new Sprite();
    sp.x = frand(0.0, 400.0);
    sp.y = frand(0.0, 240.0);
    sp.vx = frand(-60.0, 60.0);
    sp.vy = frand(-60.0, 60.0);
    sp.frame = lcg() % 8;
    sprites.push(sp);
  }

  for (let f = 0; f < frames; f++) {
    for (const sp of sprites) update(sp, dt);
  }

  let sx = 0.0;
  let sy = 0.0;
  let sf = 0;
  for (const sp of sprites) {
    sx = Math.fround(sx + sp.x);
    sy = Math.fround(sy + sp.y);
    sf = (sf + sp.frame) | 0;
  }
  if (print) console.log("sum x: " + Math.trunc(sx) + ", sum y: " + Math.trunc(sy) + ", sum frame: " + sf);
  return (sf + Math.trunc(sx) + Math.trunc(sy)) | 0;
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
