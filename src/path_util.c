#include "path_util.h"
#include <SDL3/SDL.h>
#include <string.h>

const char *path_basename_noext(const char *path, char *out, size_t outsz) {
  if (!path || !out || outsz == 0)
    return out;
  const char *slash = strrchr(path, '/');
  const char *bs = strrchr(path, '\\');
  const char *base = path;
  if (slash && slash >= base)
    base = slash + 1;
  if (bs && bs >= base)
    base = bs + 1;
  SDL_snprintf(out, outsz, "%s", base);
  char *dot = strrchr(out, '.');
  if (dot)
    *dot = '\0';
  return out;
}

void path_dirname(const char *path, char *out, size_t outsz) {
  if (!path || !out || outsz == 0)
    return;
  const char *slash = strrchr(path, '/');
  const char *bs = strrchr(path, '\\');
  const char *cut = slash;
  if (bs && (!cut || bs > cut))
    cut = bs;
  if (cut && cut > path) {
    size_t n = (size_t)(cut - path);
    if (n >= outsz)
      n = outsz - 1;
    memcpy(out, path, n);
    out[n] = '\0';
  } else if (cut == path) {
    // path begins with "/" — dirname is "/"
    SDL_snprintf(out, outsz, "/");
  } else {
    SDL_snprintf(out, outsz, ".");
  }
}
