#ifndef SPIKE_COMMON_H
#define SPIKE_COMMON_H

#include <stddef.h>
#include <stdint.h>

enum {
    SPIKE_FRAMES = 1000,
    SPIKE_SPAWN_CAPACITY = 1024,
    SPIKE_SPAWN_PER_FRAME = 32,
    SPIKE_PARTICLE_COUNT = 4096
};

#define SPIKE_DT (1.0f / 50.0f)

typedef struct SpikeRng {
    uint32_t state;
} SpikeRng;

typedef enum SpikeKernel {
    SPIKE_SPRITE_UPDATE,
    SPIKE_SPAWN_CHURN_NAIVE,
    SPIKE_SPAWN_CHURN_POOL,
    SPIKE_PARTICLES
} SpikeKernel;

typedef struct SpikeWorkload {
    SpikeKernel kernel;
    const char *name;
    size_t count;
} SpikeWorkload;

uint32_t spike_lcg(SpikeRng *rng);
float spike_frand(SpikeRng *rng, float lo, float hi);

uint32_t spike_digest_init(void);
uint32_t spike_digest_float(uint32_t digest, float value);

double spike_now_seconds(void);
int spike_parse_workload(int argc, char **argv, SpikeWorkload *workload);
void spike_print_result(const SpikeWorkload *workload, const char *variant,
    double elapsed_seconds, uint32_t digest);

#endif
