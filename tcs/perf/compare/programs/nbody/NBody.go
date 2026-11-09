package main

import (
	"fmt"
	"math"
	"os"
	"strconv"
)

// float の演算と field。n-body を float32 で計算する (body は struct の固定配列)
// 積は float32(...) で包み FMA への融合を止める
type Body struct {
	X, Y, Z, Vx, Vy, Vz, Mass float32
}

const NB = 5

func sqrt32(x float32) float32 {
	return float32(math.Sqrt(float64(x)))
}

func energy(bodies *[NB]Body) float32 {
	var e float32 = 0.0
	for i := 0; i < NB; i++ {
		b := &bodies[i]
		e = e + float32(float32(0.5*b.Mass)*(float32(b.Vx*b.Vx)+float32(b.Vy*b.Vy)+float32(b.Vz*b.Vz)))
		for j := i + 1; j < NB; j++ {
			c := &bodies[j]
			dx := b.X - c.X
			dy := b.Y - c.Y
			dz := b.Z - c.Z
			e = e - float32(b.Mass*c.Mass)/sqrt32(float32(dx*dx)+float32(dy*dy)+float32(dz*dz))
		}
	}
	return e
}

func advance(bodies *[NB]Body, dt float32) {
	for i := 0; i < NB; i++ {
		b := &bodies[i]
		for j := i + 1; j < NB; j++ {
			c := &bodies[j]
			dx := b.X - c.X
			dy := b.Y - c.Y
			dz := b.Z - c.Z
			d2 := float32(dx*dx) + float32(dy*dy) + float32(dz*dz)
			mag := dt / float32(d2*sqrt32(d2))
			bm := c.Mass * mag
			cm := b.Mass * mag
			b.Vx = b.Vx - float32(dx*bm)
			b.Vy = b.Vy - float32(dy*bm)
			b.Vz = b.Vz - float32(dz*bm)
			c.Vx = c.Vx + float32(dx*cm)
			c.Vy = c.Vy + float32(dy*cm)
			c.Vz = c.Vz + float32(dz*cm)
		}
	}
	for i := 0; i < NB; i++ {
		b := &bodies[i]
		b.X = b.X + float32(dt*b.Vx)
		b.Y = b.Y + float32(dt*b.Vy)
		b.Z = b.Z + float32(dt*b.Vz)
	}
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
	var pi float32 = 3.14159265
	solarMass := float32(4.0*pi) * pi
	var daysPerYear float32 = 365.24

	bodies := [NB]Body{
		{0.0, 0.0, 0.0, 0.0, 0.0, 0.0, solarMass},
		{4.84143144, -1.16032004, -0.103622044,
			0.00166007664 * daysPerYear, 0.00769901118 * daysPerYear, -0.0000690460016 * daysPerYear,
			0.000954791938 * solarMass},
		{8.34336671, 4.12479856, -0.403523417,
			-0.00276742510 * daysPerYear, 0.00499852801 * daysPerYear, 0.0000230417297 * daysPerYear,
			0.000285885980 * solarMass},
		{12.8943695, -15.1111514, -0.223307579,
			0.00296460137 * daysPerYear, 0.00237847173 * daysPerYear, -0.0000296589568 * daysPerYear,
			0.0000436624404 * solarMass},
		{15.3796971, -25.9193146, 0.179258772,
			0.00268067772 * daysPerYear, 0.00162824170 * daysPerYear, -0.0000951592254 * daysPerYear,
			0.0000515138902 * solarMass},
	}

	var px, py, pz float32 = 0.0, 0.0, 0.0
	for i := 0; i < NB; i++ {
		px = px + float32(bodies[i].Vx*bodies[i].Mass)
		py = py + float32(bodies[i].Vy*bodies[i].Mass)
		pz = pz + float32(bodies[i].Vz*bodies[i].Mass)
	}
	bodies[0].Vx = -px / solarMass
	bodies[0].Vy = -py / solarMass
	bodies[0].Vz = -pz / solarMass

	before := int32(energy(&bodies) * 1000000.0)
	for i := int32(0); i < n; i++ {
		advance(&bodies, 0.01)
	}
	after := int32(energy(&bodies) * 1000000.0)
	if print {
		fmt.Printf("energy before: %d\nenergy after: %d\n", before, after)
	}
	return after
}

func main() {
	n := envInt("BENCH_N", 1000)
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
