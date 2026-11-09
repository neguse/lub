"use strict";
// Float math and branches in the inner loop: mandelbrot range and 50 iterations in float (f32)
function run(n, print) {
  const fn = Math.fround(n);
  let inside = 0;
  for (let y = 0; y < n; y++) {
    const ci = Math.fround(Math.fround(Math.fround(2.0 * Math.fround(y)) / fn) - 1.0);
    for (let x = 0; x < n; x++) {
      const cr = Math.fround(Math.fround(Math.fround(2.0 * Math.fround(x)) / fn) - 1.5);
      let zr = 0.0;
      let zi = 0.0;
      let tr = 0.0;
      let ti = 0.0;
      let i = 0;
      while (i < 50 && Math.fround(tr + ti) <= 4.0) {
        zi = Math.fround(Math.fround(Math.fround(2.0 * zr) * zi) + ci);
        zr = Math.fround(Math.fround(tr - ti) + cr);
        tr = Math.fround(zr * zr);
        ti = Math.fround(zi * zi);
        i = (i + 1) | 0;
      }
      if (Math.fround(tr + ti) <= 4.0) inside = (inside + 1) | 0;
    }
  }
  if (print) console.log("inside: " + inside + " of " + Math.imul(n, n));
  return inside;
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 200 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
