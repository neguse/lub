"use strict";
// Small int arrays and branches: fannkuch-redux, single-threaded
function run(n, print) {

  const perm = new Int32Array(n);
  const perm1 = new Int32Array(n);
  const count = new Int32Array(n);
  for (let i = 0; i < n; i++) perm1[i] = i;

  let maxFlips = 0;
  let checksum = 0;
  let permCount = 0;
  let r = n;
  while (true) {
    while (r !== 1) {
      count[r - 1] = r;
      r = (r - 1) | 0;
    }

    for (let i = 0; i < n; i++) perm[i] = perm1[i];
    let flips = 0;
    let k = perm[0];
    while (k !== 0) {
      let lo = 0;
      let hi = k;
      while (lo < hi) {
        const t = perm[lo];
        perm[lo] = perm[hi];
        perm[hi] = t;
        lo = (lo + 1) | 0;
        hi = (hi - 1) | 0;
      }
      flips = (flips + 1) | 0;
      k = perm[0];
    }
    if (flips > maxFlips) maxFlips = flips;
    if (permCount % 2 === 0) checksum = (checksum + flips) | 0;
    else checksum = (checksum - flips) | 0;

    while (true) {
      if (r === n) {
        if (print) {
          console.log(checksum);
          console.log("Pfannkuchen(" + n + ") = " + maxFlips);
        }
        return checksum;
      }
      const perm0 = perm1[0];
      for (let i = 0; i < r; i++) perm1[i] = perm1[i + 1];
      perm1[r] = perm0;
      count[r]--;
      if (count[r] > 0) break;
      r = (r + 1) | 0;
    }
    permCount = (permCount + 1) | 0;
  }
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 7 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
