package main

import (
	"fmt"
	"os"
	"strconv"
)

// Speed of recursive calls. Computes fib(N) with naive double recursion
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
