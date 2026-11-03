package main

import (
	"fmt"
	"os"
	"strconv"
)

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

// 内側のループの float の演算と分岐。mandelbrot と同じ範囲と反復 (50 回) を float32 で数える
// 積は float32(...) で包み FMA への融合を止める
func run(n int32, print bool) int32 {
	var inside int32
	for y := int32(0); y < n; y++ {
		ci := 2.0*float32(y)/float32(n) - 1.0
		for x := int32(0); x < n; x++ {
			cr := 2.0*float32(x)/float32(n) - 1.5
			var zr, zi, tr, ti float32 = 0.0, 0.0, 0.0, 0.0
			i := 0
			for i < 50 && tr+ti <= 4.0 {
				zi = float32(float32(2.0*zr)*zi) + ci
				zr = tr - ti + cr
				tr = zr * zr
				ti = zi * zi
				i++
			}
			if tr+ti <= 4.0 {
				inside++
			}
		}
	}
	if print {
		fmt.Printf("inside: %d of %d\n", inside, n*n)
	}
	return inside
}

func main() {
	n := envInt("BENCH_N", 200)
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
