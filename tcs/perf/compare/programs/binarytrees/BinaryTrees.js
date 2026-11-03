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

function main() {
  const s = process.env.BENCH_N;
  const n = s === undefined ? 10 : parseInt(s, 10) | 0;
  const minDepth = 4;
  const maxDepth = n < minDepth + 2 ? minDepth + 2 : n;

  const stretch = maxDepth + 1;
  console.log("stretch tree of depth " + stretch + "\t check: " + make(stretch).check());

  const longLived = make(maxDepth);
  for (let d = minDepth; d <= maxDepth; d += 2) {
    const iterations = 1 << (maxDepth - d + minDepth);
    let check = 0;
    for (let i = 0; i < iterations; i++) check = (check + make(d).check()) | 0;
    console.log(iterations + "\t trees of depth " + d + "\t check: " + check);
  }
  console.log("long lived tree of depth " + maxDepth + "\t check: " + longLived.check());
}

main();
