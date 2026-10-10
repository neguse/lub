// path の basename / dirname (SDL の path API に無い小物)。
#pragma once
#include <stddef.h>

// 拡張子を除いた basename を out に写して返す ("a/b/Game.csproj" → "Game")。
const char *path_basename_noext(const char *path, char *out, size_t outsz);
// dirname を out に写す ("a/b/x" → "a/b"、"x" → ".")。
void path_dirname(const char *path, char *out, size_t outsz);
// 親ディレクトリを out に写す。末尾の区切りは無視し、'/' と '\' の両方を
// 区切りとして受ける ("C:\x\build\" → "C:\x"、"/a/b/" → "/a"、"x" → ".")。
void path_parent_dir(const char *path, char *out, size_t outsz);
