package main

import (
	"fmt"
	"os"
	"strconv"
)

// ゲームの典型: struct の連続した配列の particle に重力と反射と中心への吸い込みをかける
// 積は float32(...) で包み FMA への融合を止める
type Particle struct {
	Px, Py, Vx, Vy float32
}

var state int32 = 12345

func lcg() int32 {
	state = (state*1103515245 + 12345) & 0x3FFFFFFF
	return state
}

func frand(lo, hi float32) float32 {
	return lo + float32(float32(float32(lcg()%1000)*(1.0/1000.0))*(hi-lo))
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
	n := 4096
	var dt float32 = 0.02

	ps := make([]Particle, n)
	for i := 0; i < n; i++ {
		ps[i].Px = frand(100.0, 300.0)
		ps[i].Py = frand(50.0, 200.0)
		ps[i].Vx = frand(-40.0, 40.0)
		ps[i].Vy = frand(-40.0, 40.0)
	}

	for f := int32(0); f < frames; f++ {
		for i := 0; i < n; i++ {
			p := &ps[i]
			p.Vy = p.Vy + float32(98.0*dt)
			p.Px = p.Px + float32(p.Vx*dt)
			p.Py = p.Py + float32(p.Vy*dt)
			if p.Px < 0.0 {
				p.Px = 0.0
				p.Vx = -p.Vx * 0.9
			}
			if p.Px > 400.0 {
				p.Px = 400.0
				p.Vx = -p.Vx * 0.9
			}
			if p.Py < 0.0 {
				p.Py = 0.0
				p.Vy = -p.Vy * 0.9
			}
			if p.Py > 240.0 {
				p.Py = 240.0
				p.Vy = -p.Vy * 0.9
			}
			dx := p.Px - 200.0
			dy := p.Py - 120.0
			d2 := float32(dx*dx) + float32(dy*dy)
			if d2 < 400.0 {
				p.Px = p.Px + float32(dx*0.1)
				p.Py = p.Py + float32(dy*0.1)
			}
		}
	}

	var sx, sy float32 = 0.0, 0.0
	for i := 0; i < n; i++ {
		sx = sx + ps[i].Px
		sy = sy + ps[i].Py
	}
	fmt.Printf("sum x: %d, sum y: %d\n", int32(sx), int32(sy))
}
