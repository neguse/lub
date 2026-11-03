"use strict";
// Float multiply-add in a double loop with int division: spectral-norm in float (f32)
function a(i, j) {
  const ij = (i + j) | 0;
  const d = (((((Math.imul(ij, (ij + 1) | 0) / 2) | 0) + i) | 0) + 1) | 0;
  return Math.fround(1.0 / d);
}

function multiplyAv(n, v, av) {
  for (let i = 0; i < n; i++) {
    let sum = 0.0;
    for (let j = 0; j < n; j++) sum = Math.fround(sum + Math.fround(a(i, j) * v[j]));
    av[i] = sum;
  }
}

function multiplyAtv(n, v, atv) {
  for (let i = 0; i < n; i++) {
    let sum = 0.0;
    for (let j = 0; j < n; j++) sum = Math.fround(sum + Math.fround(a(j, i) * v[j]));
    atv[i] = sum;
  }
}

function multiplyAtAv(n, v, tmp, atav) {
  multiplyAv(n, v, tmp);
  multiplyAtv(n, tmp, atav);
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 100 : parseInt(s, 10) | 0;
  const u = new Float32Array(n);
  const v = new Float32Array(n);
  const tmp = new Float32Array(n);
  for (let i = 0; i < n; i++) u[i] = 1.0;
  for (let k = 0; k < 10; k++) {
    multiplyAtAv(n, u, tmp, v);
    multiplyAtAv(n, v, tmp, u);
  }
  let vBv = 0.0;
  let vv = 0.0;
  for (let i = 0; i < n; i++) {
    vBv = Math.fround(vBv + Math.fround(u[i] * v[i]));
    vv = Math.fround(vv + Math.fround(v[i] * v[i]));
  }
  const r = Math.fround(Math.sqrt(Math.fround(vBv / vv)));
  console.log("spectral norm x 1e6: " + Math.trunc(Math.fround(r * Math.fround(1000000.0))));
}

main();
