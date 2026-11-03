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
	n := benchN(30)
	fmt.Printf("fib(%d) = %d\n", n, calc(n))
}
