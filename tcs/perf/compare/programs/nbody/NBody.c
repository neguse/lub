#include <math.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct {
  float x, y, z, vx, vy, vz, mass;
} Body;

#define NB 5

static float energy(Body *b) {
  float e = 0.0f;
  for (int i = 0; i < NB; i++) {
    e = e + 0.5f * b[i].mass * (b[i].vx * b[i].vx + b[i].vy * b[i].vy + b[i].vz * b[i].vz);
    for (int j = i + 1; j < NB; j++) {
      float dx = b[i].x - b[j].x;
      float dy = b[i].y - b[j].y;
      float dz = b[i].z - b[j].z;
      e = e - (b[i].mass * b[j].mass) / sqrtf(dx * dx + dy * dy + dz * dz);
    }
  }
  return e;
}

static void advance(Body *b, float dt) {
  for (int i = 0; i < NB; i++) {
    for (int j = i + 1; j < NB; j++) {
      float dx = b[i].x - b[j].x;
      float dy = b[i].y - b[j].y;
      float dz = b[i].z - b[j].z;
      float d2 = dx * dx + dy * dy + dz * dz;
      float mag = dt / (d2 * sqrtf(d2));
      float bm = b[j].mass * mag;
      float cm = b[i].mass * mag;
      b[i].vx = b[i].vx - dx * bm;
      b[i].vy = b[i].vy - dy * bm;
      b[i].vz = b[i].vz - dz * bm;
      b[j].vx = b[j].vx + dx * cm;
      b[j].vy = b[j].vy + dy * cm;
      b[j].vz = b[j].vz + dz * cm;
    }
  }
  for (int i = 0; i < NB; i++) {
    b[i].x = b[i].x + dt * b[i].vx;
    b[i].y = b[i].y + dt * b[i].vy;
    b[i].z = b[i].z + dt * b[i].vz;
  }
}

int main(void) {
  const char *s = getenv("BENCH_N");
  int n = s ? atoi(s) : 1000;
  float pi = 3.14159265f;
  float solarMass = 4.0f * pi * pi;
  float daysPerYear = 365.24f;
  Body b[NB] = {
      {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, solarMass},
      {4.84143144f, -1.16032004f, -0.103622044f, 0.00166007664f * daysPerYear,
       0.00769901118f * daysPerYear, -0.0000690460016f * daysPerYear, 0.000954791938f * solarMass},
      {8.34336671f, 4.12479856f, -0.403523417f, -0.00276742510f * daysPerYear,
       0.00499852801f * daysPerYear, 0.0000230417297f * daysPerYear, 0.000285885980f * solarMass},
      {12.8943695f, -15.1111514f, -0.223307579f, 0.00296460137f * daysPerYear,
       0.00237847173f * daysPerYear, -0.0000296589568f * daysPerYear, 0.0000436624404f * solarMass},
      {15.3796971f, -25.9193146f, 0.179258772f, 0.00268067772f * daysPerYear,
       0.00162824170f * daysPerYear, -0.0000951592254f * daysPerYear, 0.0000515138902f * solarMass},
  };
  float px = 0.0f, py = 0.0f, pz = 0.0f;
  for (int i = 0; i < NB; i++) {
    px = px + b[i].vx * b[i].mass;
    py = py + b[i].vy * b[i].mass;
    pz = pz + b[i].vz * b[i].mass;
  }
  b[0].vx = -px / solarMass;
  b[0].vy = -py / solarMass;
  b[0].vz = -pz / solarMass;

  printf("energy before: %d\n", (int)(energy(b) * 1000000.0f));
  for (int i = 0; i < n; i++)
    advance(b, 0.01f);
  printf("energy after: %d\n", (int)(energy(b) * 1000000.0f));
  return 0;
}
