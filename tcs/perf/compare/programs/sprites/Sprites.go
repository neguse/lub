package main

import (
	"fmt"
	"os"
	"strconv"
)

// ゲームの典型: sprite を 1 つずつ確保して []*Sprite に持ち、毎フレーム動かして画面端で跳ね返す
// 積は float32(...) で包み FMA への融合を止める
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
	frames := benchN(100)
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
	fmt.Printf("sum x: %d, sum y: %d, sum frame: %d\n", int32(sx), int32(sy), sf)
}
