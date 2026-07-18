/* pd_api.h を include できない TU (本物の Lua ヘッダと lua_State が
   衝突する) 向けのログ窓口。実装は pd_shim.c */

#ifndef PERF_PD_LOG_H
#define PERF_PD_LOG_H

void perf_pd_log(const char *format, ...);

#endif
