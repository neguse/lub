#include "common.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

typedef struct Sprite {
    float x;
    float y;
    float vx;
    float vy;
    int32_t frame;
} Sprite;

typedef struct Entity {
    float x;
    float y;
    float vx;
    float vy;
} Entity;

typedef struct Particle {
    float px;
    float py;
    float vx;
    float vy;
} Particle;

typedef PerfRunResult RunResult;

static void *
checked_malloc(size_t size)
{
    void *allocation = malloc(size);

    if (allocation == NULL) {
        fprintf(stderr, "allocation failed for %zu bytes\n", size);
        exit(EXIT_FAILURE);
    }
    return allocation;
}

static inline void
update_entity(Entity *entity)
{
    entity->x = entity->x + entity->vx * PERF_DT;
    entity->y = entity->y + entity->vy * PERF_DT;
    if (entity->x < 0.0f) {
        entity->x = 0.0f;
        entity->vx = -entity->vx;
    }
    if (entity->x > 400.0f) {
        entity->x = 400.0f;
        entity->vx = -entity->vx;
    }
    if (entity->y < 0.0f) {
        entity->y = 0.0f;
        entity->vy = -entity->vy;
    }
    if (entity->y > 240.0f) {
        entity->y = 240.0f;
        entity->vy = -entity->vy;
    }
}

static void
initialize_entity(Entity *entity, PerfRng *rng)
{
    entity->x = perf_frand(rng, 0.0f, 400.0f);
    entity->y = perf_frand(rng, 0.0f, 240.0f);
    entity->vx = perf_frand(rng, -30.0f, 30.0f);
    entity->vy = perf_frand(rng, -30.0f, 30.0f);
}

static RunResult
run_sprite_update(size_t count)
{
    PerfRng rng = { UINT32_C(12345) };
    Sprite *sprites = checked_malloc(count * sizeof(*sprites));
    RunResult result;
    size_t i;
    int frame;
    double started;

    for (i = 0; i < count; ++i) {
        sprites[i].x = perf_frand(&rng, 0.0f, 400.0f);
        sprites[i].y = perf_frand(&rng, 0.0f, 240.0f);
        sprites[i].vx = perf_frand(&rng, -60.0f, 60.0f);
        sprites[i].vy = perf_frand(&rng, -60.0f, 60.0f);
        sprites[i].frame = (int32_t)(perf_lcg(&rng) % UINT32_C(8));
    }

    started = perf_now_seconds();
    for (frame = 0; frame < PERF_FRAMES; ++frame) {
        for (i = 0; i < count; ++i) {
            Sprite *sprite = &sprites[i];

            sprite->x = sprite->x + sprite->vx * PERF_DT;
            sprite->y = sprite->y + sprite->vy * PERF_DT;
            if (sprite->x < 0.0f) {
                sprite->x = 0.0f;
                sprite->vx = -sprite->vx;
            }
            if (sprite->x > 400.0f) {
                sprite->x = 400.0f;
                sprite->vx = -sprite->vx;
            }
            if (sprite->y < 0.0f) {
                sprite->y = 0.0f;
                sprite->vy = -sprite->vy;
            }
            if (sprite->y > 240.0f) {
                sprite->y = 240.0f;
                sprite->vy = -sprite->vy;
            }
            sprite->frame = (sprite->frame + 1) % 8;
        }
    }
    result.elapsed_seconds = perf_now_seconds() - started;

    result.digest = perf_digest_init();
    for (i = 0; i < count; ++i) {
        result.digest = perf_digest_float(result.digest, sprites[i].x);
        result.digest = perf_digest_float(result.digest, sprites[i].y);
        result.digest = perf_digest_float(result.digest, sprites[i].vx);
        result.digest = perf_digest_float(result.digest, sprites[i].vy);
    }

    free(sprites);
    return result;
}

static RunResult
run_spawn_churn_naive(void)
{
    PerfRng rng = { UINT32_C(12345) };
    Entity **ring = checked_malloc(PERF_SPAWN_CAPACITY * sizeof(*ring));
    RunResult result;
    size_t head = 0;
    size_t i;
    int frame;
    double started;

    for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
        ring[i] = checked_malloc(sizeof(*ring[i]));
        initialize_entity(ring[i], &rng);
    }

    started = perf_now_seconds();
    for (frame = 0; frame < PERF_FRAMES; ++frame) {
        for (i = 0; i < PERF_SPAWN_PER_FRAME; ++i) {
            size_t slot = (head + i) % PERF_SPAWN_CAPACITY;

            free(ring[slot]);
            ring[slot] = checked_malloc(sizeof(*ring[slot]));
            initialize_entity(ring[slot], &rng);
        }
        head = (head + PERF_SPAWN_PER_FRAME) % PERF_SPAWN_CAPACITY;
        for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
            update_entity(ring[(head + i) % PERF_SPAWN_CAPACITY]);
        }
    }
    result.elapsed_seconds = perf_now_seconds() - started;

    result.digest = perf_digest_init();
    for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
        Entity *entity = ring[(head + i) % PERF_SPAWN_CAPACITY];

        result.digest = perf_digest_float(result.digest, entity->x);
        result.digest = perf_digest_float(result.digest, entity->y);
        result.digest = perf_digest_float(result.digest, entity->vx);
        result.digest = perf_digest_float(result.digest, entity->vy);
    }

    for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
        free(ring[i]);
    }
    free(ring);
    return result;
}

static RunResult
run_spawn_churn_pool(void)
{
    PerfRng rng = { UINT32_C(12345) };
    Entity *ring = checked_malloc(PERF_SPAWN_CAPACITY * sizeof(*ring));
    RunResult result;
    size_t head = 0;
    size_t i;
    int frame;
    double started;

    for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
        initialize_entity(&ring[i], &rng);
    }

    started = perf_now_seconds();
    for (frame = 0; frame < PERF_FRAMES; ++frame) {
        for (i = 0; i < PERF_SPAWN_PER_FRAME; ++i) {
            initialize_entity(
                &ring[(head + i) % PERF_SPAWN_CAPACITY], &rng);
        }
        head = (head + PERF_SPAWN_PER_FRAME) % PERF_SPAWN_CAPACITY;
        for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
            update_entity(&ring[(head + i) % PERF_SPAWN_CAPACITY]);
        }
    }
    result.elapsed_seconds = perf_now_seconds() - started;

    result.digest = perf_digest_init();
    for (i = 0; i < PERF_SPAWN_CAPACITY; ++i) {
        Entity *entity = &ring[(head + i) % PERF_SPAWN_CAPACITY];

        result.digest = perf_digest_float(result.digest, entity->x);
        result.digest = perf_digest_float(result.digest, entity->y);
        result.digest = perf_digest_float(result.digest, entity->vx);
        result.digest = perf_digest_float(result.digest, entity->vy);
    }

    free(ring);
    return result;
}

static RunResult
run_particles(void)
{
    PerfRng rng = { UINT32_C(12345) };
    Particle *particles = checked_malloc(
        PERF_PARTICLE_COUNT * sizeof(*particles));
    RunResult result;
    size_t i;
    int frame;
    double started;

    for (i = 0; i < PERF_PARTICLE_COUNT; ++i) {
        particles[i].px = perf_frand(&rng, 100.0f, 300.0f);
        particles[i].py = perf_frand(&rng, 50.0f, 200.0f);
        particles[i].vx = perf_frand(&rng, -40.0f, 40.0f);
        particles[i].vy = perf_frand(&rng, -40.0f, 40.0f);
    }

    started = perf_now_seconds();
    for (frame = 0; frame < PERF_FRAMES; ++frame) {
        for (i = 0; i < PERF_PARTICLE_COUNT; ++i) {
            Particle *particle = &particles[i];
            float dx;
            float dy;
            float d2;

            particle->vy = particle->vy + 98.0f * PERF_DT;
            particle->px = particle->px + particle->vx * PERF_DT;
            particle->py = particle->py + particle->vy * PERF_DT;
            if (particle->px < 0.0f) {
                particle->px = 0.0f;
                particle->vx = -particle->vx * 0.9f;
            }
            if (particle->px > 400.0f) {
                particle->px = 400.0f;
                particle->vx = -particle->vx * 0.9f;
            }
            if (particle->py < 0.0f) {
                particle->py = 0.0f;
                particle->vy = -particle->vy * 0.9f;
            }
            if (particle->py > 240.0f) {
                particle->py = 240.0f;
                particle->vy = -particle->vy * 0.9f;
            }
            dx = particle->px - 200.0f;
            dy = particle->py - 120.0f;
            d2 = dx * dx + dy * dy;
            if (d2 < 400.0f) {
                particle->px = particle->px + dx * 0.1f;
                particle->py = particle->py + dy * 0.1f;
            }
        }
    }
    result.elapsed_seconds = perf_now_seconds() - started;

    result.digest = perf_digest_init();
    for (i = 0; i < PERF_PARTICLE_COUNT; ++i) {
        result.digest = perf_digest_float(result.digest, particles[i].px);
        result.digest = perf_digest_float(result.digest, particles[i].py);
        result.digest = perf_digest_float(result.digest, particles[i].vx);
        result.digest = perf_digest_float(result.digest, particles[i].vy);
    }

    free(particles);
    return result;
}

RunResult
perf_native_dispatch(const PerfWorkload *workload)
{
    switch (workload->kernel) {
    case PERF_SPRITE_UPDATE:
        return run_sprite_update(workload->count);
    case PERF_SPAWN_CHURN_NAIVE:
        return run_spawn_churn_naive();
    case PERF_SPAWN_CHURN_POOL:
        return run_spawn_churn_pool();
    case PERF_PARTICLES:
    default:
        return run_particles();
    }
}

#ifndef PERF_NO_MAIN
int
main(int argc, char **argv)
{
    PerfWorkload workload;
    RunResult result;

    if (!perf_parse_workload(argc, argv, &workload)) {
        return EXIT_FAILURE;
    }

    result = perf_native_dispatch(&workload);
    perf_print_result(&workload, "native", result.elapsed_seconds,
        result.digest);
    return EXIT_SUCCESS;
}
#endif
