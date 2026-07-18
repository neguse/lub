#ifndef PERF_COMMON_H
#define PERF_COMMON_H

#include <stddef.h>
#include <stdint.h>

enum {
    PERF_FRAMES = 1000,
    PERF_SPAWN_CAPACITY = 1024,
    PERF_SPAWN_PER_FRAME = 32,
    PERF_PARTICLE_COUNT = 4096
};

#define PERF_DT (1.0f / 50.0f)

typedef struct PerfRng {
    uint32_t state;
} PerfRng;

typedef enum PerfKernel {
    PERF_SPRITE_UPDATE,
    PERF_SPAWN_CHURN_NAIVE,
    PERF_SPAWN_CHURN_POOL,
    PERF_PARTICLES
} PerfKernel;

typedef struct PerfWorkload {
    PerfKernel kernel;
    const char *name;
    size_t count;
} PerfWorkload;

typedef struct PerfRunResult {
    double elapsed_seconds;
    uint32_t digest;
} PerfRunResult;

uint32_t perf_lcg(PerfRng *rng);
float perf_frand(PerfRng *rng, float lo, float hi);

uint32_t perf_digest_init(void);
uint32_t perf_digest_float(uint32_t digest, float value);

/* perf_now_seconds はホストでは common.c (POSIX clock)、Playdate では
   perf/playdate/ の shim が提供する */
double perf_now_seconds(void);
int perf_parse_workload(int argc, char **argv, PerfWorkload *workload);
void perf_print_result(const PerfWorkload *workload, const char *variant,
    double elapsed_seconds, uint32_t digest);

PerfRunResult perf_native_dispatch(const PerfWorkload *workload);
PerfRunResult perf_aot_hash_dispatch(const PerfWorkload *workload);
PerfRunResult perf_aot_slot_dispatch(const PerfWorkload *workload);

#endif
