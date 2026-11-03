"use strict";
// Recursive calls: naive doubly recursive fib(N), int32 via |0
function calc(n) {
  if (n < 2) return n;
  return (calc((n - 1) | 0) + calc((n - 2) | 0)) | 0;
}

const s = process.env.BENCH_N;
const n = s === undefined ? 30 : parseInt(s, 10) | 0;
console.log("fib(" + n + ") = " + calc(n));
