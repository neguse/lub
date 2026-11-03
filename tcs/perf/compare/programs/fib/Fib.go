package main

import (
	"fmt"
	"os"
	"strconv"
)

// 再帰呼び出しの速さ。fib(N) を素朴な二重再帰で計算する
func calc(n int32) int32 {
	if n < 2 {
		return n
	}
	return calc(n-1) + calc(n-2)
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
	f := calc(n)
	if print {
		fmt.Printf("fib(%d) = %d\n", n, f)
	}
	return f
}

func main() {
	n := envInt("BENCH_N", 30)
	// BENCH_REPS 回くり返す。次の回の入力を前の回の結果に依存させ、compiler が回を省けないようにする (結果は -1 にならない)
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
