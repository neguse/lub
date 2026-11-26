"use strict";
// Small object allocation and GC: binary-trees, single-threaded, one object per node
class TreeNode {
  constructor(left, right) {
    this.left = left;
    this.right = right;
  }

  check() {
    if (this.left === null) return 1;
    return (1 + this.left.check() + this.right.check()) | 0;
  }
}

function make(depth) {
  if (depth === 0) return new TreeNode(null, null);
  return new TreeNode(make(depth - 1), make(depth - 1));
}

function run(n, print) {
  const minDepth = 4;
  const maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

  const stretch = maxDepth + 1;
  const stretchCheck = make(stretch).check();
  if (print) console.log("stretch tree of depth " + stretch + "\t check: " + stretchCheck);

  let digest = stretchCheck;
  const longLived = make(maxDepth);
  for (let d = minDepth; d <= maxDepth; d += 2) {
    const iterations = 1 << (maxDepth - d + minDepth);
    let check = 0;
    for (let i = 0; i < iterations; i++) check = (check + make(d).check()) | 0;
    if (print) console.log(iterations + "\t trees of depth " + d + "\t check: " + check);
    digest = (digest + check) | 0;
  }
  const longCheck = longLived.check();
  if (print) console.log("long lived tree of depth " + maxDepth + "\t check: " + longCheck);
  return (digest + longCheck) | 0;
}

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 10 : parseInt(s, 10) | 0;
  // Repeat BENCH_REPS times; each input depends on the previous result so the JIT cannot drop or hoist runs (never -1)
  const rs = process.env.BENCH_REPS;
  const reps = rs === undefined ? 1 : parseInt(rs, 10) | 0;
  let prev = 0;
  for (let r = 0; r < reps; r++) prev = run(prev === -1 ? n + 1 : n, r === reps - 1);
}

main();
