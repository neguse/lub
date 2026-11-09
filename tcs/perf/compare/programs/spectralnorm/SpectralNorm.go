package main

import (
	"fmt"
	"math"
	"os"
	"strconv"
)

// 二重ループの float の積和と int の除算。spectral-norm を float32 で計算する
// 積は float32(...) で包み FMA への融合を止める
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
