// Smoke test for tcs_pipeline_next_frame.
// The fake tcs (lub-test fake-tcs, tests/lub-test/Tests/DevTools.cs) emits a
// frame of unknown kind, a vm_batch, and build_failed in a single write. The
// body of the unknown frame contains lines that look like frame headers and
// UTF-8 multibyte characters, so unless the body is skipped by its byte count,
// false diffs appear from there.
#include "../../src/tcs_build.h"
#include <SDL3/SDL.h>
#include <string.h>

int main(int argc, char *argv[]) {
  (void)argc;
  (void)argv;
  static const struct {
    TcsFrameKind kind;
    const char *body;
  } want[] = {
      {TCS_FRAME_VM_BATCH, "QUJDREVG"},
      {TCS_FRAME_BUILD_FAILED, "oops!"},
  };
  const int nwant = (int)SDL_arraysize(want);
  const char *args[] = {"-o", "unused.tcsb"};
  TcsPipeline p;
  if (!tcs_vm_pipeline_start(&p, args, 2)) {
    SDL_Log("FAIL: tcs_vm_pipeline_start failed");
    return 1;
  }
  int got = 0, failures = 0;
  for (int i = 0; i < 1000 && got < nwant; i++) {
    TcsFrameKind kind;
    char *body = NULL;
    size_t len = 0;
    if (!tcs_pipeline_next_frame(&p, &kind, &body, &len)) {
      SDL_Delay(10);
      continue;
    }
    if (kind != want[got].kind || len != strlen(want[got].body) ||
        memcmp(body, want[got].body, len) != 0) {
      SDL_Log("FAIL: frame %d: kind %d, body \"%s\"", got, (int)kind, body);
      failures++;
    }
    SDL_free(body);
    got++;
  }
  tcs_pipeline_stop(&p);
  if (got < nwant) {
    SDL_Log("FAIL: got %d of %d frames", got, nwant);
    return 1;
  }
  if (failures > 0)
    return 1;
  SDL_Log(
      "PASS: vm_batch and build_failed in order, the unknown frame skipped");
  return 0;
}
