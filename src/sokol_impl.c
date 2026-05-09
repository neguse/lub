// sokol_gfx.h includes its implementation in the same header, gated by
// SOKOL_GFX_IMPL. The implementation section is *outside* the include guard,
// so defining SOKOL_GFX_IMPL in a translation unit that also pulls
// sokol_gfx.h transitively from multiple headers causes duplicate symbol
// definitions. Keep the implementation in this single dedicated TU.
#define SOKOL_GFX_IMPL
#include "sokol_gfx.h"
