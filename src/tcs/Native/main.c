/* The program of a command line tool written in TinyC# and made into C by
   `tcs c --lib`: tcs itself, and lub-gen.
   Includes the generated C (tcs.c: Compiler, CBackend, VmBackend, Api and Cli),
   implements what it asks of the outside (Api/TcsFiles.cs on Native/files.c,
   Cli/CliProcess.cs), gives each command line argument to the Arg entry, and
   calls the Step entry until it returns the exit code. Each return from an
   entry is a GC frame boundary of the C runtime.
   Files shipped with the tool (the library in lib/, the C in runtime/ that
   tcs c embeds) are read from the dir in the TCS_HOME environment variable or
   from the executable's dir.
   compile: cc $(src/tcs/cflags.sh) -I <dir of tcs.c> main.c -o tcs -lm
   Another tool passes its generated C file name in TCS_MAIN_SOURCE and its
   entries (tcs_entry_<Class>_<Method>) in TCS_MAIN_ARG and TCS_MAIN_STEP (used
   by lub-gen) */
#ifndef TCS_MAIN_SOURCE
#define TCS_MAIN_SOURCE "tcs.c"
#endif
#ifndef TCS_MAIN_ARG
#define TCS_MAIN_ARG tcs_entry_Tcs_Cli_Arg
#endif
#ifndef TCS_MAIN_STEP
#define TCS_MAIN_STEP tcs_entry_Tcs_Cli_Step
#endif
#include TCS_MAIN_SOURCE

#include "files_tcs.c"

#include <time.h>
#ifdef _WIN32
#include <fcntl.h>
#endif

static char *main_home;

TcsString *tcs_host_tcsfiles_shipped(TcsString *name) {
  char *rel = tcs_files_cstring(name);
  size_t n = strlen(main_home) + strlen(rel) + 1;
  char *full = tcs_files_alloc(n);
  snprintf(full, n, "%s%s", main_home, rel);
  free(rel);
  TcsString *text = tcs_files_read_string(full);
  free(full);
  return text;
}

void tcs_host_cliprocess_out(TcsString *text) {
  fwrite(text->data, 1, text->length, stdout);
}

void tcs_host_cliprocess_err(TcsString *text) {
  fwrite(text->data, 1, text->length, stderr);
}

void tcs_host_cliprocess_flush(void) { fflush(stdout); }

void tcs_host_cliprocess_sleep(int32_t milliseconds) {
#ifdef _WIN32
  Sleep((DWORD)milliseconds);
#else
  struct timespec ts;
  ts.tv_sec = milliseconds / 1000;
  ts.tv_nsec = (long)(milliseconds % 1000) * 1000000L;
  nanosleep(&ts, NULL);
#endif
}

/* If TCS_HEAP=1, print the maximum managed heap size between steps to stderr
 * (the gap between entries is a GC frame boundary) */
int main(int argc, char **argv) {
#ifdef _WIN32
  /* Write without converting newlines, as the .NET tcs does (text mode turns \n
   * into \r\n) */
  _setmode(_fileno(stdin), _O_BINARY);
  _setmode(_fileno(stdout), _O_BINARY);
  _setmode(_fileno(stderr), _O_BINARY);
#endif
  main_home = tcs_files_home(argc > 0 ? argv[0] : NULL);
  tcs_lib_init();
  for (int i = 1; i < argc; i++)
    TCS_MAIN_ARG(tcs_files_string(argv[i], strlen(argv[i])));
  const char *heap = getenv("TCS_HEAP");
  size_t peak = 0;
  int steps = 0;
  int code;
  do {
    code = TCS_MAIN_STEP();
    steps++;
    size_t now = tcs_lib_heap_bytes();
    if (now > peak)
      peak = now;
  } while (code < 0);
  if (heap != NULL && heap[0] == '1')
    fprintf(stderr, "steps %d, peak managed heap between steps %zu bytes\n",
            steps, peak);
  fflush(stdout);
  return code;
}
