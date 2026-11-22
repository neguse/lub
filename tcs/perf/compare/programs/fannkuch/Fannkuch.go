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

// Array operations and integer branches. Same procedure as fannkuch-redux (single-thread version)
func run(n int32, print bool) int32 {
	perm := make([]int32, n)
	perm1 := make([]int32, n)
	count := make([]int32, n)
	for i := int32(0); i < n; i++ {
		perm1[i] = i
	}

	var maxFlips, checksum, permCount int32
	r := n
	for {
		for r != 1 {
			count[r-1] = r
			r--
		}

		for i := int32(0); i < n; i++ {
			perm[i] = perm1[i]
		}
		var flips int32
		k := perm[0]
		for k != 0 {
			lo := int32(0)
			hi := k
			for lo < hi {
				t := perm[lo]
				perm[lo] = perm[hi]
				perm[hi] = t
				lo++
				hi--
			}
			flips++
			k = perm[0]
		}
		if flips > maxFlips {
			maxFlips = flips
		}
		if permCount%2 == 0 {
			checksum += flips
		} else {
			checksum -= flips
		}

		for {
			if r == n {
				if print {
					fmt.Println(checksum)
					fmt.Printf("Pfannkuchen(%d) = %d\n", n, maxFlips)
				}
				return checksum
			}
			perm0 := perm1[0]
			for i := int32(0); i < r; i++ {
				perm1[i] = perm1[i+1]
			}
			perm1[r] = perm0
			count[r]--
			if count[r] > 0 {
				break
			}
			r++
		}
		permCount++
	}
}

func main() {
	n := envInt("BENCH_N", 7)
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
