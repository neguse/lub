"use strict";
// Recursive calls: naive doubly recursive fib(N), int32 via |0
function calc(n) {
  if (n < 2) return n;
  return (calc((n - 1) | 0) + calc((n - 2) | 0)) | 0;
}

function run(n, print) {
  const f = calc(n);
  if (print) console.log("fib(" + n + ") = " + f);
  return f;
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 30 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
