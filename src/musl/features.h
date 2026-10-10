/* musl の内部の features.h の代わり (third_party/musl の data header が読む)。
   OS に features.h があればそれも読む。 */
#ifndef LUB_MUSL_FEATURES_H
#define LUB_MUSL_FEATURES_H

#if defined(__has_include_next)
#if __has_include_next(<features.h>)
#include_next <features.h>
#endif
#endif

#ifndef hidden
#define hidden
#endif

/* musl を libc に持つ Emscripten で、libc の同じ名前の表と衝突させない。
   関数の名前は libm.h で付け替える。 */
#define __exp2f_data lub___exp2f_data
#define __logf_data lub___logf_data
#define __log2f_data lub___log2f_data
#define __powf_log2_data lub___powf_log2_data

#endif
