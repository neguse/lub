/* lua_static の全ての TU に強制 include する (CMakeLists.txt)。
   Lua の ^ とその定数畳み込みが使う luai_numpow を lub_powf にする。
   指数 2 を乗算にするのは llimits.h の既定と同じ。 */
#include "lub_math.h"
#define luai_numpow(L, a, b) ((void)L, (b == 2) ? (a) * (a) : lub_powf(a, b))
