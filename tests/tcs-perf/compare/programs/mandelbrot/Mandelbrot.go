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

// Float arithmetic and branches in the inner loop. Counts over the same range and iterations (50) as mandelbrot, in float32
// Products are wrapped in float32(...) to prevent fusion into FMA
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
