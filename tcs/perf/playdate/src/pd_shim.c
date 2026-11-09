/* common.h の perf_now_seconds と pd_log.h の perf_pd_log を
   Playdate API で提供する。各 job の直前に resetElapsedTime するため
   float 精度のタイマーで足りる */

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "pd_shim.h"

#include "common.h"
#include "pd_log.h"

PlaydateAPI *perf_pd;

#if TARGET_PLAYDATE
/* -nostartfiles のため crt が提供する _init/_fini が無い。newlib の
   __libc_init_array/__libc_fini_array (exit 経路) が参照するので空定義を置く */
void
_init(void)
{
}

void
_fini(void)
{
}
#endif

double
perf_now_seconds(void)
{
    return (double)perf_pd->system->getElapsedTime();
}

/* console と Data/<bundleID>/results.csv の両方へ出す。ヘッドレスの
   シミュレータや実機からの結果回収はファイル側が正 (job 毎に append +
   close して途中クラッシュでも残るようにする) */
void
perf_pd_log(const char *format, ...)
{
    char line[256];
    va_list args;
    SDFile *file;

    va_start(args, format);
    vsnprintf(line, sizeof line, format, args);
    va_end(args);
    perf_pd->system->logToConsole("%s", line);

    file = perf_pd->file->open("results.csv", kFileAppend);
    if (file != NULL) {
        perf_pd->file->write(file, line, (unsigned int)strlen(line));
        perf_pd->file->write(file, "\n", 1);
        perf_pd->file->close(file);
    }
}
