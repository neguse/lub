package main

import (
	"fmt"
	"os"
	"strconv"
)

// 小さな object の確保と GC。node は 1 つずつ確保する (arena は使わない)
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

func benchN(def int32) int32 {
	s, ok := os.LookupEnv("BENCH_N")
	if !ok {
		return def
	}
	v, err := strconv.Atoi(s)
	if err != nil {
		panic(err)
	}
	return int32(v)
}

func main() {
	n := benchN(10)
	minDepth := int32(4)
	maxDepth := n
	if n < minDepth+2 {
		maxDepth = minDepth + 2
	}

	stretch := maxDepth + 1
	fmt.Printf("stretch tree of depth %d\t check: %d\n", stretch, makeTree(stretch).Check())

	longLived := makeTree(maxDepth)
	for d := minDepth; d <= maxDepth; d += 2 {
		iterations := int32(1) << (maxDepth - d + minDepth)
		var check int32
		for i := int32(0); i < iterations; i++ {
			check += makeTree(d).Check()
		}
		fmt.Printf("%d\t trees of depth %d\t check: %d\n", iterations, d, check)
	}
	fmt.Printf("long lived tree of depth %d\t check: %d\n", maxDepth, longLived.Check())
}
