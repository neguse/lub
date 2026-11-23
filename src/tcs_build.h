#pragma once
// tcs (TinyC#) child process: the frame stream of tcs vm --watch, and tcs
// check. Implemented in tcs_build.c.
#include <SDL3/SDL.h>
#include <stdbool.h>

typedef struct TcsPipeline {
  SDL_Process *proc; // tcs --watch (on Linux / macOS, the sh that guards tcs).
                     // Stopped at shutdown
  void *job; // Windows Job Object (HANDLE). Contains tcs and its children
  // The part of the tcs stdout (frame stream) not yet cut out
  char *buf;
  size_t len, cap;
} TcsPipeline;

typedef enum TcsFrameKind {
  // A change that cannot be migrated with a diff.
  // The body is a newline-terminated reason line, and the output of that build
  // has been fully written.
  // Later diffs target the VM started from that output
  TCS_FRAME_RESTART_REQUIRED,
  // A tcs vm --watch diff.
  // The body is one base64 line of the batch passed to tvm_apply_batch, plus a
  // newline
  TCS_FRAME_VM_BATCH,
  // A build with compile errors.
  // The body is newline-terminated diagnostic lines, and the output stays as
  // the previous build
  TCS_FRAME_BUILD_FAILED,
  // A change applied with the next vm_batch but worth reporting (e.g. a change
  // to an instance field initializer).
  // The body is newline-terminated lines
  TCS_FRAME_RELOAD_NOTES,
} TcsFrameKind;

// Takes one frame that tcs writes on each subsequent rebuild, in arrival order
// (does not block). Frames of unknown kinds are skipped. *out is allocated with
// SDL_malloc (NUL-terminated), and the caller frees it with SDL_free.
bool tcs_pipeline_next_frame(TcsPipeline *p, TcsFrameKind *kind, char **out,
                             size_t *out_len);
void tcs_pipeline_stop(TcsPipeline *p);

// Starts `tcs vm <args> --watch` in the background (does not wait for output).
// Frames are received with tcs_pipeline_next_frame.
bool tcs_vm_pipeline_start(TcsPipeline *p, const char *const *args, int nargs);
// Whether tcs is still running.
bool tcs_pipeline_alive(TcsPipeline *p);

// Passes cs_path (.csproj; inputs = all *.cs under the same dir and the cs-lib
// implementation, --ref is cs-lib/lub_stub.cs) to tcs check and waits until it
// finishes. tcs diagnostics go straight to stdout / stderr. true if there are
// no diagnostics.
bool tcs_check(const char *cs_path);

// List of .cs files in the csproj directory dir (paths relative to dir,
// '/'-separated, ascending). Following the implicit glob (**/*.cs) of SDK-style
// csproj, subdirectories are also walked. bin / obj and directories whose names
// start with '.' (including .lub) are not walked. Writes the count to *count.
// Free with tcs_free_sources.
char **tcs_glob_sources(const char *dir, int *count);
void tcs_free_sources(char **sources, int count);

// Resolves the cs-lib directory and writes it to out.
// The priority is the LUB_CS_LIB environment variable (no fallback if set),
// cs-lib in cwd, then cs-lib one level above the executable. false if not
// found.
bool tcs_resolve_cs_lib(char *out, size_t outsz);
