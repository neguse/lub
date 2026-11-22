package main

import (
	"fmt"
	"os"
	"strconv"
)

// A typical game workload: allocates sprites one at a time, holds them in []*Sprite, moves them every frame, and bounces them off the screen edges
// Products are wrapped in float32(...) to prevent fusion into FMA
type Sprite struct {
	X, Y, Vx, Vy float32
	Frame        int32
}

var state int32 = 12345

func lcg() int32 {
	state = (state*1103515245 + 12345) & 0x3FFFFFFF
	return state
}

func frand(lo, hi float32) float32 {
	return lo + float32(float32(float32(lcg()%1000)*(1.0/1000.0))*(hi-lo))
}

func update(s *Sprite, dt float32) {
	s.X = s.X + float32(s.Vx*dt)
	s.Y = s.Y + float32(s.Vy*dt)
	if s.X < 0.0 {
		s.X = 0.0
		s.Vx = -s.Vx
	}
	if s.X > 400.0 {
		s.X = 400.0
		s.Vx = -s.Vx
	}
	if s.Y < 0.0 {
		s.Y = 0.0
		s.Vy = -s.Vy
	}
	if s.Y > 240.0 {
		s.Y = 240.0
		s.Vy = -s.Vy
	}
	s.Frame = (s.Frame + 1) % 8
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

func run(frames int32, print bool) int32 {
	state = 12345
	count := 1024
	var dt float32 = 0.02

	sprites := []*Sprite{}
	for i := 0; i < count; i++ {
		sp := &Sprite{}
		sp.X = frand(0.0, 400.0)
		sp.Y = frand(0.0, 240.0)
		sp.Vx = frand(-60.0, 60.0)
		sp.Vy = frand(-60.0, 60.0)
		sp.Frame = lcg() % 8
		sprites = append(sprites, sp)
	}

	for f := int32(0); f < frames; f++ {
		for _, sp := range sprites {
			update(sp, dt)
		}
	}

	var sx, sy float32 = 0.0, 0.0
	var sf int32
	for _, sp := range sprites {
		sx = sx + sp.X
		sy = sy + sp.Y
		sf = sf + sp.Frame
	}
	if print {
		fmt.Printf("sum x: %d, sum y: %d, sum frame: %d\n", int32(sx), int32(sy), sf)
	}
	return sf + int32(sx) + int32(sy)
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
