/* Provides perf_now_seconds of common.h and perf_pd_log of pd_log.h
   with the Playdate API. resetElapsedTime is called right before each job, so
   a float-precision timer is enough */

#include <stdarg.h>
#include <stdio.h>
#include <string.h>

#include "pd_shim.h"

#include "common.h"
#include "pd_log.h"

PlaydateAPI *perf_pd;

#if TARGET_PLAYDATE
/* With -nostartfiles there are no _init/_fini provided by crt. newlib's
   __libc_init_array/__libc_fini_array (exit path) reference them, so empty
   definitions are provided */
void _init(void) {}

void _fini(void) {}
#endif

double perf_now_seconds(void) {
  return (double)perf_pd->system->getElapsedTime();
}

/* Writes to both the console and Data/<bundleID>/results.csv. For collecting
   results from the headless simulator or hardware, the file is the source of
   truth (append + close per job so the results survive a crash midway) */
void perf_pd_log(const char *format, ...) {
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
