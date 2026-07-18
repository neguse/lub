/* ホスト CLI 専用部 (POSIX clock / argv / stdout)。
   OS 非依存の kernel 共通部は core.c */

#define _POSIX_C_SOURCE 200809L

#include "common.h"

#include <errno.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

double
perf_now_seconds(void)
{
    struct timespec now;

    if (clock_gettime(CLOCK_MONOTONIC, &now) != 0) {
        perror("clock_gettime");
        exit(EXIT_FAILURE);
    }
    return (double)now.tv_sec + (double)now.tv_nsec / 1000000000.0;
}

static int
parse_count(const char *text, size_t *count)
{
    char *end;
    unsigned long parsed;

    errno = 0;
    parsed = strtoul(text, &end, 10);
    if (errno != 0 || end == text || *end != '\0' || parsed > SIZE_MAX) {
        return 0;
    }
    *count = (size_t)parsed;
    return 1;
}

int
perf_parse_workload(int argc, char **argv, PerfWorkload *workload)
{
    size_t count;

    if (argc != 3 || !parse_count(argv[2], &count)) {
        fprintf(stderr,
            "usage: %s {sprite_update|spawn_churn_naive|"
            "spawn_churn_pool|particles} N\n",
            argv[0]);
        return 0;
    }

    workload->name = argv[1];
    workload->count = count;
    if (strcmp(argv[1], "sprite_update") == 0
        && (count == 256 || count == 1024 || count == 4096)) {
        workload->kernel = PERF_SPRITE_UPDATE;
        return 1;
    }
    if (strcmp(argv[1], "spawn_churn_naive") == 0
        && count == PERF_SPAWN_CAPACITY) {
        workload->kernel = PERF_SPAWN_CHURN_NAIVE;
        return 1;
    }
    if (strcmp(argv[1], "spawn_churn_pool") == 0
        && count == PERF_SPAWN_CAPACITY) {
        workload->kernel = PERF_SPAWN_CHURN_POOL;
        return 1;
    }
    if (strcmp(argv[1], "particles") == 0
        && count == PERF_PARTICLE_COUNT) {
        workload->kernel = PERF_PARTICLES;
        return 1;
    }

    fprintf(stderr, "invalid kernel/N combination: %s %zu\n", argv[1], count);
    return 0;
}

void
perf_print_result(const PerfWorkload *workload, const char *variant,
    double elapsed_seconds, uint32_t digest)
{
    double ms_per_frame = elapsed_seconds * 1000.0 / PERF_FRAMES;

    printf("%s,%s,%zu,%.9f,%08" PRIx32 "\n",
        workload->name, variant, workload->count, ms_per_frame, digest);
}
