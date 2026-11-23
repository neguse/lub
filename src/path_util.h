// basename / dirname for paths (small helpers missing from SDL's path API).
#pragma once
#include <stddef.h>

// Copy the basename without its extension to out and return it
// ("a/b/Game.csproj" → "Game").
const char *path_basename_noext(const char *path, char *out, size_t outsz);
// Copy the dirname to out ("a/b/x" → "a/b", "x" → ".").
void path_dirname(const char *path, char *out, size_t outsz);
// Copy the parent directory to out.
// Trailing separators are ignored, and both '/' and '\' are accepted as
// separators ("C:\x\build\" → "C:\x", "/a/b/" → "/a", "x" → ".").
void path_parent_dir(const char *path, char *out, size_t outsz);
