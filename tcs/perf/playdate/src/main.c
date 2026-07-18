/* Playdate 実機/シミュレータで perf kernel を実測するハーネス。
   update 1 回につき 1 job (変種×kernel) を実行し、結果 CSV を
   logToConsole へ出す。出力形式はホスト run.sh と同じ:
   kernel,variant,N,ms_per_frame,digest */

#include "pd_api.h"

#include "common.h"
#include "pd_log.h"
#include "pd_shim.h"

/* interp_runner.c (pd_api.h と lua.h の型衝突のため別 TU) */
void perf_interp_run(const char *kernel_name, unsigned count);

typedef enum Variant {
    VARIANT_NATIVE,
    VARIANT_AOT_HASH,
    VARIANT_AOT_SLOT,
    VARIANT_INTERP
} Variant;

static const char *const VARIANT_NAMES[] = {
    "native", "aot-hash", "aot-slot", "interp"
};

typedef struct Job {
    Variant variant;
    PerfKernel kernel;
    const char *kernel_name;
    unsigned count;
} Job;

#define JOB_KERNELS(variant) \
    { variant, PERF_SPRITE_UPDATE, "sprite_update", 256 }, \
    { variant, PERF_SPRITE_UPDATE, "sprite_update", 1024 }, \
    { variant, PERF_SPRITE_UPDATE, "sprite_update", 4096 }, \
    { variant, PERF_SPAWN_CHURN_NAIVE, "spawn_churn_naive", 1024 }, \
    { variant, PERF_SPAWN_CHURN_POOL, "spawn_churn_pool", 1024 }, \
    { variant, PERF_PARTICLES, "particles", 4096 }

static const Job JOBS[] = {
    JOB_KERNELS(VARIANT_NATIVE),
    JOB_KERNELS(VARIANT_AOT_SLOT),
    JOB_KERNELS(VARIANT_AOT_HASH),
    JOB_KERNELS(VARIANT_INTERP),
};

enum { JOB_COUNT = sizeof(JOBS) / sizeof(JOBS[0]) };

static int next_job;

static void
run_c_job(const Job *job)
{
    PerfWorkload workload;
    PerfRunResult result;

    workload.kernel = job->kernel;
    workload.name = job->kernel_name;
    workload.count = job->count;

    switch (job->variant) {
    case VARIANT_AOT_HASH:
        result = perf_aot_hash_dispatch(&workload);
        break;
    case VARIANT_AOT_SLOT:
        result = perf_aot_slot_dispatch(&workload);
        break;
    case VARIANT_NATIVE:
    default:
        result = perf_native_dispatch(&workload);
        break;
    }

    perf_pd_log("%s,%s,%u,%.9f,%08x",
        job->kernel_name, VARIANT_NAMES[job->variant], job->count,
        result.elapsed_seconds * 1000.0 / PERF_FRAMES,
        (unsigned)result.digest);
}

static int
update(void *userdata)
{
    PlaydateAPI *pd = userdata;
    int done;

    if (next_job < JOB_COUNT) {
        const Job *job = &JOBS[next_job];

        pd->system->resetElapsedTime();
        if (job->variant == VARIANT_INTERP) {
            perf_interp_run(job->kernel_name, job->count);
        }
        else {
            run_c_job(job);
        }
        next_job++;
        if (next_job == JOB_COUNT) {
            perf_pd_log("perf: done (%d jobs)", JOB_COUNT);
        }
    }

    done = next_job < JOB_COUNT ? next_job : JOB_COUNT;
    pd->graphics->clear(kColorWhite);
    pd->graphics->fillRect(20, 110, 360 * done / JOB_COUNT, 20, kColorBlack);
    pd->graphics->drawRect(20, 110, 360, 20, kColorBlack);
    return 1;
}

#ifdef _WINDLL
__declspec(dllexport)
#endif
int
eventHandler(PlaydateAPI *pd, PDSystemEvent event, uint32_t arg)
{
    (void)arg;

    if (event == kEventInit) {
        perf_pd = pd;
        next_job = 0;
        pd->system->setUpdateCallback(update, pd);
    }
    return 0;
}
