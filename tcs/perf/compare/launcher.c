// Small launcher that runs a child process once and measures its wall time and
// peak RSS. Forking directly from Python counts the Python memory from before
// exec in the child's peak RSS, so the child is started from a small C process.
// usage: launcher RESULT_FILE CPU|-1 PROGRAM [ARGS...]
// Writes one line "wall_seconds peak_rss_kib exit_code" to RESULT_FILE. The
// child's stdout / stderr are inherited as is
#define _GNU_SOURCE
#include <sched.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/resource.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

int main(int argc, char **argv) {
  if (argc < 4) {
    fprintf(stderr, "usage: launcher RESULT_FILE CPU|-1 PROGRAM [ARGS...]\n");
    return 2;
  }
  int cpu = atoi(argv[2]);
  struct timespec t0, t1;
  clock_gettime(CLOCK_MONOTONIC, &t0);
  pid_t pid = fork();
  if (pid < 0) {
    perror("fork");
    return 2;
  }
  if (pid == 0) {
    if (cpu >= 0) {
      cpu_set_t set;
      CPU_ZERO(&set);
      CPU_SET(cpu, &set);
      sched_setaffinity(0, sizeof set, &set);
    }
    execvp(argv[3], argv + 3);
    perror(argv[3]);
    _exit(127);
  }
  int status;
  struct rusage ru;
  if (wait4(pid, &status, 0, &ru) < 0) {
    perror("wait4");
    return 2;
  }
  clock_gettime(CLOCK_MONOTONIC, &t1);
  double wall =
      (double)(t1.tv_sec - t0.tv_sec) + (double)(t1.tv_nsec - t0.tv_nsec) / 1e9;
  int code = WIFEXITED(status) ? WEXITSTATUS(status) : 128 + WTERMSIG(status);
  FILE *f = fopen(argv[1], "w");
  if (!f) {
    perror(argv[1]);
    return 2;
  }
  fprintf(f, "%.9f %ld %d\n", wall, ru.ru_maxrss, code);
  fclose(f);
  return 0;
}
