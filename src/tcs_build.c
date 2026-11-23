// tcs (TinyC#) child process: lub launches tcs.
// With tcs vm --watch running in the background, tcs recompiles on every .cs
// save and writes to stdout, in order, the diffs to apply to the running VM (or
// restart_required, which asks to recreate the VM, or compile errors). The lub
// side owns the child process lifecycle and cutting frames out of that stream
// (applying them is in src/vm_player.c). tcs check collects the .csproj inputs,
// launches, and waits until it finishes.
#include "tcs_build.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <string.h>

#include "path_util.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

static bool file_exists(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info);
}

// Whether dotnet is on PATH
static bool dotnet_on_path(void) {
  const char *path = SDL_getenv("PATH");
  if (!path)
    return false;
#ifdef _WIN32
  const char sep = ';';
  const char *exe = "dotnet.exe";
#else
  const char sep = ':';
  const char *exe = "dotnet";
#endif
  const char *p = path;
  while (*p) {
    const char *end = SDL_strchr(p, sep);
    size_t len = end ? (size_t)(end - p) : SDL_strlen(p);
    if (len > 0 && len < 700) {
      char cand[768];
      SDL_snprintf(cand, sizeof(cand), "%.*s/%s", (int)len, p, exe);
      if (file_exists(cand))
        return true;
    }
    if (!end)
      break;
    p = end + 1;
  }
  return false;
}

// Resolves the command that launches tcs.
// Priority:
//   1. LUB_TCS environment variable (space-separated command prefix; quoting
//      is not supported)
//   2. <exe>/tcs/tcs -> tcs converted to C with tcs2c (in the release bundle,
//      and what scripts/build-tcs-native.sh puts in the build dir)
//   3. <cwd>/tcs -> dotnet run --project ... -- (when dotnet is on PATH)
//   4. <exe>/../tcs -> same as above
//   5. tcs/SelfHost/out/native/tcs -> the C tcs built in the checkout (when
//      dotnet is not on PATH)
// Returns: the number of entries put into argv (0 = resolution failed).
// Allocated in storage, equivalent to strdup.
static int resolve_tcs_cmd(char storage[][512], int max_args,
                           const char **argv) {
  const char *env = SDL_getenv("LUB_TCS");
  if (env && env[0]) {
    int n = 0;
    char buf[1024];
    SDL_strlcpy(buf, env, sizeof(buf));
    for (char *tok = strtok(buf, " "); tok && n < max_args - 1;
         tok = strtok(NULL, " ")) {
      SDL_strlcpy(storage[n], tok, 512);
      argv[n] = storage[n];
      n++;
    }
    return n;
  }
  const char *roots[2] = {".", NULL};
  char exe_root[768] = "";
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
#ifdef _WIN32
    SDL_snprintf(storage[0], 512, "%stcs\\tcs.exe", base_path);
#else
    SDL_snprintf(storage[0], 512, "%stcs/tcs", base_path);
#endif
    if (file_exists(storage[0])) {
      argv[0] = storage[0];
      return 1;
    }
    path_parent_dir(base_path, exe_root, sizeof(exe_root));
    roots[1] = exe_root;
  }
  // If there is no dotnet, use the C tcs built in the checkout (the output of
  // tcs/SelfHost/native.sh)
  for (int i = 0; !dotnet_on_path() && i < 2; i++) {
    if (!roots[i])
      continue;
#ifdef _WIN32
    SDL_snprintf(storage[0], 512, "%s/tcs/SelfHost/out/native/tcs.exe",
                 roots[i]);
#else
    SDL_snprintf(storage[0], 512, "%s/tcs/SelfHost/out/native/tcs", roots[i]);
#endif
    if (file_exists(storage[0])) {
      argv[0] = storage[0];
      return 1;
    }
  }
  for (int i = 0; i < 2; i++) {
    if (!roots[i])
      continue;
    char proj[768];
    SDL_snprintf(proj, sizeof(proj), "%s/tcs/Transpiler/Transpiler.csproj",
                 roots[i]);
    if (!file_exists(proj))
      continue;
    SDL_strlcpy(storage[0], "dotnet", 512);
    SDL_strlcpy(storage[1], "run", 512);
    SDL_strlcpy(storage[2], "--project", 512);
    SDL_strlcpy(storage[3], proj, 512);
    SDL_strlcpy(storage[4], "--", 512);
    for (int k = 0; k < 5; k++)
      argv[k] = storage[k];
    return 5;
  }
  return 0;
}

bool tcs_resolve_cs_lib(char *out, size_t outsz) {
  const char *env = SDL_getenv("LUB_CS_LIB");
  if (env && env[0]) {
    SDL_PathInfo info;
    if (SDL_GetPathInfo(env, &info) && info.type == SDL_PATHTYPE_DIRECTORY) {
      SDL_strlcpy(out, env, outsz);
      return true;
    }
    SDL_Log("LUB_CS_LIB is not a directory: %s", env);
    return false;
  }
  const char *cands[2] = {"cs-lib", NULL};
  char exe_dir[900] = "";
  const char *base_path = SDL_GetBasePath();
  if (base_path) {
    char root[768];
    path_parent_dir(base_path, root, sizeof(root));
    SDL_snprintf(exe_dir, sizeof(exe_dir), "%s/cs-lib", root);
    cands[1] = exe_dir;
  }
  for (int i = 0; i < 2; i++) {
    SDL_PathInfo info;
    if (cands[i] && SDL_GetPathInfo(cands[i], &info) &&
        info.type == SDL_PATHTYPE_DIRECTORY) {
      SDL_strlcpy(out, cands[i], outsz);
      return true;
    }
  }
  return false;
}

static bool str_ends_with(const char *s, const char *suffix) {
  size_t ls = SDL_strlen(s), lf = SDL_strlen(suffix);
  return ls >= lf && SDL_strcmp(s + ls - lf, suffix) == 0;
}

typedef struct SourceList {
  char **v;
  int n, cap;
} SourceList;

typedef struct SourceWalk {
  SourceList *list;
  const char *abs; // Directory being scanned (absolute / relative to cwd)
  const char *rel; // Relative to dir ("" = dir itself)
  int depth;
} SourceWalk;

static void walk_sources(SourceList *list, const char *abs, const char *rel,
                         int depth);

static SDL_EnumerationResult source_entry(void *userdata, const char *dirname,
                                          const char *fname) {
  (void)dirname;
  SourceWalk *w = (SourceWalk *)userdata;
  char abs[1024], rel[1024];
  SDL_snprintf(abs, sizeof(abs), "%s/%s", w->abs, fname);
  if (w->rel[0])
    SDL_snprintf(rel, sizeof(rel), "%s/%s", w->rel, fname);
  else
    SDL_strlcpy(rel, fname, sizeof(rel));
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(abs, &info))
    return SDL_ENUM_CONTINUE;
  if (info.type == SDL_PATHTYPE_DIRECTORY) {
    // DefaultItemExcludes of dotnet (bin / obj / hidden directories).
    // bin / obj are excluded at any depth so that the generated files of nested
    // projects (obj/*.AssemblyInfo.cs) are not picked up
    if (fname[0] != '.' && SDL_strcmp(fname, "bin") != 0 &&
        SDL_strcmp(fname, "obj") != 0)
      walk_sources(w->list, abs, rel, w->depth + 1);
  } else if (info.type == SDL_PATHTYPE_FILE && str_ends_with(fname, ".cs")) {
    if (w->list->n == w->list->cap) {
      int cap = w->list->cap ? w->list->cap * 2 : 16;
      char **grown = (char **)SDL_realloc(w->list->v, cap * sizeof(char *));
      if (!grown)
        return SDL_ENUM_FAILURE;
      w->list->v = grown;
      w->list->cap = cap;
    }
    w->list->v[w->list->n++] = SDL_strdup(rel);
  }
  return SDL_ENUM_CONTINUE;
}

static void walk_sources(SourceList *list, const char *abs, const char *rel,
                         int depth) {
  if (depth > 32) // Prevents an endless loop on symlink cycles
    return;
  SourceWalk w = {list, abs, rel, depth};
  SDL_EnumerateDirectory(abs, source_entry, &w);
}

static int cmp_str(const void *a, const void *b) {
  return SDL_strcmp(*(char *const *)a, *(char *const *)b);
}

char **tcs_glob_sources(const char *dir, int *count) {
  SourceList list = {0};
  walk_sources(&list, dir, "", 0);
  if (list.n > 1)
    SDL_qsort(list.v, (size_t)list.n, sizeof(char *), cmp_str);
  *count = list.n;
  return list.v;
}

void tcs_free_sources(char **sources, int count) {
  for (int i = 0; i < count; i++)
    SDL_free(sources[i]);
  SDL_free(sources);
}

#ifdef _WIN32
// Puts tcs into a Job Object that stops all processes in it when its handle is
// closed. However lub ends, the OS closes the handle, so tcs does not survive
// even a forced termination. Children also join the same Job, so the Transpiler
// child of dotnet run also stops. The handle is not inherited (if tcs held it,
// it could not be closed).
static void *tcs_job_adopt(SDL_Process *proc) {
  HANDLE job = CreateJobObjectW(NULL, NULL);
  if (!job) {
    SDL_Log("tcs: CreateJobObject failed (%lu)", GetLastError());
    return NULL;
  }
  JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = {0};
  info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
  bool ok = SetInformationJobObject(job, JobObjectExtendedLimitInformation,
                                    &info, sizeof(info));
  DWORD pid = (DWORD)SDL_GetNumberProperty(SDL_GetProcessProperties(proc),
                                           SDL_PROP_PROCESS_PID_NUMBER, 0);
  HANDLE child = OpenProcess(PROCESS_SET_QUOTA | PROCESS_TERMINATE, FALSE, pid);
  ok = ok && child && AssignProcessToJobObject(job, child);
  DWORD error = ok ? 0 : GetLastError();
  if (child)
    CloseHandle(child);
  if (!ok) {
    SDL_Log("tcs: could not put tcs in a job object (%lu); it may outlive lub",
            error);
    CloseHandle(job);
    return NULL;
  }
  return job;
}
#endif

static void tcs_job_close(TcsPipeline *p) {
#ifdef _WIN32
  if (p->job)
    CloseHandle(p->job);
  p->job = NULL;
#else
  (void)p;
#endif
}

#define TCS_ARGV_MAX 256

#ifndef _WIN32
// On Linux / macOS, tcs runs under a guard sh (in place of the Windows Job
// Object). The stdin of sh is a pipe whose write end only lub holds, and the OS
// closes it however lub ends (even by a signal or SIGKILL). When it closes, sh
// sends SIGTERM to tcs (dotnet run forwards it to the Transpiler child).
// SIGTERM to sh (tcs_pipeline_stop) is also forwarded to tcs, and if tcs exits
// by itself, sh exits with the same exit code. The stdin of a background list
// is /dev/null, so the pipe is copied to fd 3 and passed to the guard.
static const char GUARD_SCRIPT[] =
    "tcs=\n"
    "trap 'kill $tcs 2>/dev/null' INT TERM HUP\n"
    "exec 3<&0\n"
    "\"$@\" 3<&- </dev/null &\n"
    "tcs=$!\n"
    "{ while read -r _; do :; done; kill $tcs 2>/dev/null; } <&3 &\n"
    "guard=$!\n"
    "wait $tcs\n"
    "status=$?\n"
    "kill $guard 2>/dev/null\n"
    "exit $status\n";
#endif

// Runs argv (NULL-terminated) in the background as tcs --watch.
// stdout is received as the frame stream, and stderr (tcs logs and diagnostics)
// goes to the terminal.
static bool spawn_watch(TcsPipeline *p, const char **argv) {
#ifndef _WIN32
  const char *guarded[TCS_ARGV_MAX + 4];
  int n = 0;
  guarded[n++] = "/bin/sh";
  guarded[n++] = "-c";
  guarded[n++] = GUARD_SCRIPT;
  guarded[n++] = "lub-tcs-guard"; // $0
  for (int i = 0; argv[i]; i++)
    guarded[n++] = argv[i];
  guarded[n] = NULL;
  argv = guarded;
#endif
  SDL_PropertiesID props = SDL_CreateProperties();
  SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER,
                         (void *)argv);
  SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER,
                        SDL_PROCESS_STDIO_APP);
#ifndef _WIN32
  SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDIN_NUMBER,
                        SDL_PROCESS_STDIO_APP);
#endif
  p->proc = SDL_CreateProcessWithProperties(props);
  SDL_DestroyProperties(props);
  if (!p->proc) {
    SDL_Log("tcs spawn failed: %s", SDL_GetError());
    return false;
  }
#ifdef _WIN32
  p->job = tcs_job_adopt(p->proc);
#endif
  return true;
}

// Appends the inputs of entry (.csproj) to argv starting at argv[n], and
// returns the next free position. Inputs are all *.cs under dir (following the
// implicit glob of SDK-style csproj; tcs_glob_sources), the cs-lib
// implementation, and the --ref stub. -1 if there are no inputs to add. argv
// points to the stub, so the caller owns it.
static int append_inputs(const char *dir, const char **argv, int n, char *stub,
                         size_t stub_sz) {
  char cs_lib[900];
  bool has_cs_lib = tcs_resolve_cs_lib(cs_lib, sizeof(cs_lib));
  bool has_stub = false;
  if (has_cs_lib) {
    SDL_snprintf(stub, stub_sz, "%s/lub_stub.cs", cs_lib);
    has_stub = file_exists(stub);
  }
  if (!has_stub)
    SDL_Log("cs-lib/lub_stub.cs not found (set LUB_CS_LIB to the cs-lib "
            "directory); compiling without lub API stub");

  int glob_count = 0;
  char **globbed = tcs_glob_sources(dir, &glob_count);
  int inputs = 0;
  for (int i = 0; i < glob_count; i++) {
    if (n >= TCS_ARGV_MAX - 16) {
      SDL_Log("tcs argv full: dropped %d sample source(s)", glob_count - i);
      break;
    }
    char full[1400];
    SDL_snprintf(full, sizeof(full), "%s/%s", dir, globbed[i]);
    argv[n] =
        SDL_strdup(full); // Fine to live until process exit (leak accepted)
    n++;
    inputs++;
  }
  tcs_free_sources(globbed, glob_count);
  if (inputs == 0) {
    SDL_Log("no .cs sources under %s", dir);
    return -1;
  }

  // Adds all cs-lib implementation sources (all *.cs except lub_stub.cs)
  // unconditionally. The stub is declarations only (--ref), but the
  // implementation modules are subject to the check.
  if (has_cs_lib) {
    int lib_count = 0;
    char **lib = SDL_GlobDirectory(cs_lib, NULL, 0, &lib_count);
    if (lib) {
      for (int i = 0; i < lib_count; i++) {
        if (n >= TCS_ARGV_MAX - 16) {
          SDL_Log("tcs argv full: dropped remaining cs-lib sources");
          break;
        }
        if (!str_ends_with(lib[i], ".cs"))
          continue;
        const char *base = SDL_strrchr(lib[i], '/');
        base = base ? base + 1 : lib[i];
        if (SDL_strcmp(base, "lub_stub.cs") == 0)
          continue;
        char full[1200];
        SDL_snprintf(full, sizeof(full), "%s/%s", cs_lib, lib[i]);
        argv[n] =
            SDL_strdup(full); // Fine to live until process exit (leak accepted)
        n++;
      }
      SDL_free(lib);
    }
  }

  if (has_stub) {
    argv[n++] = "--ref";
    argv[n++] = stub;
  }
  return n;
}

bool tcs_vm_pipeline_start(TcsPipeline *p, const char *const *args, int nargs) {
  if (!p)
    return false;
  SDL_zerop(p);
  char storage[16][512];
  const char *argv[TCS_ARGV_MAX];
  int n = resolve_tcs_cmd(storage, 16, argv);
  if (n == 0) {
    SDL_Log("tcs not found: set LUB_TCS, or run from a lub checkout "
            "(tcs/Transpiler/Transpiler.csproj)");
    return false;
  }
  argv[n++] = "vm";
  for (int i = 0; i < nargs && n < TCS_ARGV_MAX - 2; i++)
    argv[n++] = args[i];
  argv[n++] = "--watch";
  argv[n] = NULL;
  return spawn_watch(p, argv);
}

bool tcs_pipeline_alive(TcsPipeline *p) {
  int exit_code = 0;
  if (!p || !p->proc)
    return false;
  if (SDL_WaitProcess(p->proc, false, &exit_code)) {
    SDL_Log("tcs exited (exit=%d)", exit_code);
    return false;
  }
  return true;
}

bool tcs_check(const char *cs_path) {
  char dir[512];
  path_dirname(cs_path, dir, sizeof(dir));

  char storage[16][512];
  const char *argv[TCS_ARGV_MAX];
  int n = resolve_tcs_cmd(storage, 16, argv);
  if (n == 0) {
    SDL_Log("tcs not found: set LUB_TCS, or run from a lub checkout "
            "(tcs/Transpiler/Transpiler.csproj)");
    return false;
  }
  argv[n++] = "check";
  char stub[960] = "";
  n = append_inputs(dir, argv, n, stub, sizeof(stub));
  if (n < 0)
    return false;
  argv[n] = NULL;

  SDL_Process *proc = SDL_CreateProcess(argv, false);
  if (!proc) {
    SDL_Log("tcs spawn failed: %s", SDL_GetError());
    return false;
  }
  int exit_code = 1;
  SDL_WaitProcess(proc, true, &exit_code);
  SDL_DestroyProcess(proc);
  return exit_code == 0;
}

// Stream format: a sequence of pairs of a `@@tcs_<kind> <body byte count>\n`
// line and the body
static const char FRAME_PREFIX[] = "@@tcs_";

// If buf[0..line) (excluding the newline) is a frame header, returns kind and
// the body byte count
static bool parse_frame_header(const char *buf, size_t line, const char **kind,
                               size_t *kind_len, size_t *body) {
  size_t prefix = sizeof(FRAME_PREFIX) - 1;
  if (line <= prefix || memcmp(buf, FRAME_PREFIX, prefix) != 0)
    return false;
  const char *space = (const char *)memchr(buf + prefix, ' ', line - prefix);
  if (!space || space == buf + prefix)
    return false;
  const char *digits = space + 1, *end = buf + line;
  if (digits == end || end - digits > 12)
    return false;
  *body = 0;
  for (const char *c = digits; c < end; c++) {
    if (*c < '0' || *c > '9')
      return false;
    *body = *body * 10 + (size_t)(*c - '0');
  }
  *kind = buf + prefix;
  *kind_len = (size_t)(space - *kind);
  return true;
}

static bool kind_is(const char *kind, size_t kind_len, const char *name) {
  return kind_len == SDL_strlen(name) && memcmp(kind, name, kind_len) == 0;
}

bool tcs_pipeline_next_frame(TcsPipeline *p, TcsFrameKind *kind, char **out,
                             size_t *out_len) {
  if (!p || !p->proc)
    return false;
  SDL_IOStream *io = (SDL_IOStream *)SDL_GetPointerProperty(
      SDL_GetProcessProperties(p->proc), SDL_PROP_PROCESS_STDOUT_POINTER, NULL);
  char tmp[16384];
  size_t got;
  while (io && (got = SDL_ReadIO(io, tmp, sizeof(tmp))) > 0) {
    if (p->len + got > p->cap) {
      size_t cap = p->cap ? p->cap : sizeof(tmp);
      while (cap < p->len + got)
        cap *= 2;
      char *grown = (char *)SDL_realloc(p->buf, cap);
      if (!grown) {
        SDL_Log("tcs: frame stream dropped: out of memory");
        return false;
      }
      p->buf = grown;
      p->cap = cap;
    }
    memcpy(p->buf + p->len, tmp, got);
    p->len += got;
  }

  while (p->len > 0) {
    char *nl = (char *)memchr(p->buf, '\n', p->len);
    if (!nl)
      return false;
    size_t head = (size_t)(nl - p->buf) + 1;
    const char *name;
    size_t name_len, body;
    if (!parse_frame_header(p->buf, head - 1, &name, &name_len, &body)) {
      // Lines that are not frame headers (unexpected output) are discarded and
      // skipped
      SDL_Log("tcs: ignoring stdout line: %.*s", (int)(head - 1), p->buf);
      memmove(p->buf, p->buf + head, p->len - head);
      p->len -= head;
      continue;
    }
    if (p->len < head + body)
      return false;
    bool known = true;
    if (kind_is(name, name_len, "restart_required"))
      *kind = TCS_FRAME_RESTART_REQUIRED;
    else if (kind_is(name, name_len, "vm_batch"))
      *kind = TCS_FRAME_VM_BATCH;
    else if (kind_is(name, name_len, "build_failed"))
      *kind = TCS_FRAME_BUILD_FAILED;
    else if (kind_is(name, name_len, "reload_notes"))
      *kind = TCS_FRAME_RELOAD_NOTES;
    else
      known = false;
    if (!known) {
      // A kind added by a newer tcs.
      // Skip it together with its body
      SDL_Log("tcs: skipping unknown frame %.*s (%zu bytes)", (int)name_len,
              name, body);
      memmove(p->buf, p->buf + head + body, p->len - head - body);
      p->len -= head + body;
      continue;
    }
    char *chunk = (char *)SDL_malloc(body + 1);
    if (!chunk)
      return false;
    memcpy(chunk, p->buf + head, body);
    chunk[body] = '\0';
    memmove(p->buf, p->buf + head + body, p->len - head - body);
    p->len -= head + body;
    *out = chunk;
    *out_len = body;
    return true;
  }
  return false;
}

void tcs_pipeline_stop(TcsPipeline *p) {
  if (!p || !p->proc)
    return;
#ifdef _WIN32
  // On Windows, a non-forced kill does not reach console processes (it only
  // sends WM_CLOSE and returns success)
  SDL_KillProcess(p->proc, true);
#else
  SDL_KillProcess(p->proc, false);
#endif
  tcs_job_close(p);
  SDL_DestroyProcess(p->proc);
  p->proc = NULL;
  SDL_free(p->buf);
  p->buf = NULL;
  p->len = p->cap = 0;
}
