#pragma once
// How the tcs VM player (src/vm_player.c) collects inputs when running from a
// .csproj. lub does not evaluate the csproj as MSBuild. It reads only the
// basename (entry class) and the ProjectReferences with the attribute
// Lub="Source". Inputs are all *.cs under the csproj directory and the
// referenced directories, plus the cs-lib implementation.
// *.ref.cs are not compiled; they are passed with tcs --ref (type-only
// declarations).
#include <SDL3/SDL.h>
#include <stdbool.h>

#define VM_PROJECT_MAX_DIRS 32

typedef struct VmProject {
  char entry[256];     // entry class (basename of the csproj)
  char out_tcsb[640];  // <dir>/.lub/<Entry>.tcsb (written by tcs vm)
  char list_path[640]; // <dir>/.lub/<Entry>.inputs (list of inputs; tcs vm
                       // --inputs-from)
  char stub[960];      // cs-lib/lub_stub.cs ("" if none)
  char cs_lib[900];    // cs-lib ("" if none)
  char dirs[VM_PROJECT_MAX_DIRS]
           [512]; // Directories to search for sources (entry first)
  int ndirs;
  // Compile the game source with --trace (an app opened with App.Use; the
  // opener observes it)
  bool trace;
  char export_[320]; // Value of --export built by vm_project_args
  char *list_text;   // The last written list
  char *refs_text;   // The last found list of *.ref.cs (newline-separated)
  char *refs_split;  // Copy of refs_text split by vm_project_args
  // Native extensions next to *.ref.cs (include/lub/lub_vm_extension.h).
  // Searched on the first scan
  char extensions[VM_PROJECT_MAX_DIRS][640];
  int nextensions;
} VmProject;

// Reads the csproj and follows the references.
// If a marked referenced csproj is missing, logs and returns false.
bool vm_project_open(VmProject *proj, const char *csproj);
// Rescans the directories, and if the list changed, rewrites it and returns
// true.
bool vm_project_scan(VmProject *proj);
// Puts the arguments for tcs vm (--inputs-from, --ref, -o, --export, --trace)
// into args and returns the count. When trace is set, the source directories
// are passed to --trace. The strings are owned by proj (valid until the next
// vm_project_args).
int vm_project_args(VmProject *proj, const char **args, int max);
void vm_project_close(VmProject *proj);
