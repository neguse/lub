/* OS 非依存の kernel 共通部。Playdate デバイスビルドにもそのまま入る */

#include "common.h"

#include <string.h>

_Static_assert(sizeof(float) == 4, "perf requires 32-bit float");
_Static_assert(sizeof(uint32_t) == 4, "perf requires 32-bit uint32_t");

uint32_t
perf_lcg(PerfRng *rng)
{
    rng->state = (rng->state * UINT32_C(1103515245) + UINT32_C(12345))
        & UINT32_C(0x3fffffff);
    return rng->state;
}

float
perf_frand(PerfRng *rng, float lo, float hi)
{
    float scaled = (float)(perf_lcg(rng) % UINT32_C(1000))
        * (1.0f / 1000.0f);
    return lo + scaled * (hi - lo);
}

uint32_t
perf_digest_init(void)
{
    return UINT32_C(2166136261);
}

uint32_t
perf_digest_float(uint32_t digest, float value)
{
    uint32_t bits;
    unsigned int shift;

    memcpy(&bits, &value, sizeof(bits));
    for (shift = 0; shift < 32; shift += 8) {
        digest ^= (bits >> shift) & UINT32_C(0xff);
        digest *= UINT32_C(16777619);
    }
    return digest;
}
