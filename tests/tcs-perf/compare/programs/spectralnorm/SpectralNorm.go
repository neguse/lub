package main

import (
	"fmt"
	"math"
	"os"
	"strconv"
)

// Float multiply-add in a double loop and int division. Computes spectral-norm in float32
// Products are wrapped in float32(...) to prevent fusion into FMA
func A(i, j int32) float32 {
	ij := i + j
	return 1.0 / float32(ij*(ij+1)/2+i+1)
}

func multiplyAv(n int32, v, av []float32) {
	for i := int32(0); i < n; i++ {
		var sum float32 = 0.0
		for j := int32(0); j < n; j++ {
			sum = sum + float32(A(i, j)*v[j])
		}
		av[i] = sum
	}
}

func multiplyAtv(n int32, v, atv []float32) {
	for i := int32(0); i < n; i++ {
		var sum float32 = 0.0
		for j := int32(0); j < n; j++ {
			sum = sum + float32(A(j, i)*v[j])
		}
		atv[i] = sum
	}
}

func multiplyAtAv(n int32, v, tmp, atav []float32) {
	multiplyAv(n, v, tmp)
	multiplyAtv(n, tmp, atav)
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
	u := make([]float32, n)
	v := make([]float32, n)
	tmp := make([]float32, n)
	for i := int32(0); i < n; i++ {
		u[i] = 1.0
	}
	for k := 0; k < 10; k++ {
		multiplyAtAv(n, u, tmp, v)
		multiplyAtAv(n, v, tmp, u)
	}
	var vBv, vv float32 = 0.0, 0.0
	for i := int32(0); i < n; i++ {
		vBv = vBv + float32(u[i]*v[i])
		vv = vv + float32(v[i]*v[i])
	}
	result := int32(float32(math.Sqrt(float64(vBv/vv))) * 1000000.0)
	if print {
		fmt.Printf("spectral norm x 1e6: %d\n", result)
	}
	return result
}

func main() {
	n := envInt("BENCH_N", 100)
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
