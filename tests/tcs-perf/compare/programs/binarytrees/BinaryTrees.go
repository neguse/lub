package main

import (
	"fmt"
	"os"
	"strconv"
)

// Small object allocation and GC. Nodes are allocated one at a time (no arena)
type TreeNode struct {
	Left, Right *TreeNode
}

func (t *TreeNode) Check() int32 {
	if t.Left == nil {
		return 1
	}
	return 1 + t.Left.Check() + t.Right.Check()
}

func makeTree(depth int32) *TreeNode {
	if depth == 0 {
		return &TreeNode{nil, nil}
	}
	return &TreeNode{makeTree(depth - 1), makeTree(depth - 1)}
}

func envInt(name string, def int32) int32 {
	s, ok := os.LookupEnv(name)
	if !ok {
		return def
	}
	v, err := strconv.Atoi(s)
	if err != nil {
		panic(err)
	}
	return int32(v)
}

func run(n int32, print bool) int32 {
	minDepth := int32(4)
	maxDepth := n
	if n < minDepth+2 {
		maxDepth = minDepth + 2
	}

	stretch := maxDepth + 1
	stretchCheck := makeTree(stretch).Check()
	if print {
		fmt.Printf("stretch tree of depth %d\t check: %d\n", stretch, stretchCheck)
	}
	digest := stretchCheck

	longLived := makeTree(maxDepth)
	for d := minDepth; d <= maxDepth; d += 2 {
		iterations := int32(1) << (maxDepth - d + minDepth)
		var check int32
		for i := int32(0); i < iterations; i++ {
			check += makeTree(d).Check()
		}
		if print {
			fmt.Printf("%d\t trees of depth %d\t check: %d\n", iterations, d, check)
		}
		digest += check
	}
	longCheck := longLived.Check()
	if print {
		fmt.Printf("long lived tree of depth %d\t check: %d\n", maxDepth, longCheck)
	}
	return digest + longCheck
}

func main() {
	n := envInt("BENCH_N", 10)
	// Repeats BENCH_REPS times. Each repetition's input depends on the previous result so the compiler cannot skip repetitions (the result is never -1)
	reps := envInt("BENCH_REPS", 1)
	var prev int32
	for r := int32(0); r < reps; r++ {
		if prev == -1 {
			prev = run(n+1, r == reps-1)
		} else {
			prev = run(n, r == reps-1)
		}
	}
}
