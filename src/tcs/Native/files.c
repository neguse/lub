/* File operations that tcs asks of the OS (the functions of Api/TcsFiles.cs),
   on C strings and byte counts. It does not depend on how the caller holds
   strings: Native/files_tcs.c puts the functions of a C module made by `tcs c`
   on top of it. Include it in the one C file of the program (the functions are
   static). The .NET implementation with the same behavior is Dotnet/Files.cs */
#ifndef TCS_FILES_C
#define TCS_FILES_C

#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

#ifdef _WIN32
#include <direct.h>
#include <io.h>
#include <process.h>
#include <windows.h>
#define tcs_files_getpid _getpid
#else
#include <limits.h>
#include <unistd.h>
#define tcs_files_getpid getpid
#endif
#ifdef __APPLE__
#include <mach-o/dyld.h>
#endif

/* A program uses only some of these functions */
#if defined(__GNUC__) || defined(__clang__)
#define TCS_FILES_FN __attribute__((unused)) static
#else
#define TCS_FILES_FN static
#endif

TCS_FILES_FN void *tcs_files_alloc(size_t size) {
  void *p = malloc(size);
  if (p == NULL) {
    fputs("tcs: out of memory\n", stderr);
    abort();
  }
  return p;
}

TCS_FILES_FN int tcs_files_is_file(const char *path) {
  struct stat st;
  return stat(path, &st) == 0 && (st.st_mode & S_IFMT) == S_IFREG;
}

/* The contents of a file without a UTF-8 byte order mark (as .NET's
 * File.ReadAllText does). malloc'd, with a 0 after it. NULL if it is not a
 * file or cannot be read */
TCS_FILES_FN char *tcs_files_read(const char *path, size_t *length) {
  if (!tcs_files_is_file(path))
    return NULL;
  FILE *f = fopen(path, "rb");
  if (f == NULL)
    return NULL;
  size_t cap = 65536, len = 0;
  char *buf = tcs_files_alloc(cap + 1);
  for (;;) {
    size_t got = fread(buf + len, 1, cap - len, f);
    len += got;
    if (len < cap)
      break;
    cap *= 2;
    buf = realloc(buf, cap + 1);
    if (buf == NULL) {
      fputs("tcs: out of memory\n", stderr);
      abort();
    }
  }
  int failed = ferror(f);
  fclose(f);
  if (failed) {
    free(buf);
    return NULL;
  }
  if (len >= 3 && (unsigned char)buf[0] == 0xEF &&
      (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF) {
    len -= 3;
    memmove(buf, buf + 3, len);
  }
  buf[len] = 0;
  *length = len;
  return buf;
}

/* Whether two paths refer to the same existing file: device and inode on POSIX,
 * volume and file index on Windows (links and spelling differences count as the
 * same file) */
TCS_FILES_FN int tcs_files_same(const char *na, const char *nb) {
  int same = 0;
#ifdef _WIN32
  HANDLE ha =
      CreateFileA(na, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                  NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
  HANDLE hb =
      CreateFileA(nb, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                  NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
  BY_HANDLE_FILE_INFORMATION ia, ib;
  if (ha != INVALID_HANDLE_VALUE && hb != INVALID_HANDLE_VALUE &&
      GetFileInformationByHandle(ha, &ia) &&
      GetFileInformationByHandle(hb, &ib))
    same = ia.dwVolumeSerialNumber == ib.dwVolumeSerialNumber &&
           ia.nFileIndexHigh == ib.nFileIndexHigh &&
           ia.nFileIndexLow == ib.nFileIndexLow;
  if (ha != INVALID_HANDLE_VALUE)
    CloseHandle(ha);
  if (hb != INVALID_HANDLE_VALUE)
    CloseHandle(hb);
#else
  struct stat sa, sb;
  if (stat(na, &sa) == 0 && stat(nb, &sb) == 0)
    same = sa.st_dev == sb.st_dev && sa.st_ino == sb.st_ino;
#endif
  return same;
}

TCS_FILES_FN int tcs_files_is_sep(char c) {
#ifdef _WIN32
  return c == '/' || c == '\\';
#else
  return c == '/';
#endif
}

/* Makes the path absolute and removes `.`, `..`, and repeated separators (same
 * form as .NET's Path.GetFullPath). The return value is malloc'd */
#ifdef _WIN32
/* On Windows, leave it to the OS (drive-less `\x`, drive-relative `C:x`, and
 * UNC `\\server\share` are also resolved the same as .NET) */
TCS_FILES_FN char *tcs_files_full_path(const char *path) {
  DWORD need = GetFullPathNameA(path, 0, NULL, NULL);
  char *out = tcs_files_alloc((need > 0 ? need : strlen(path)) + 2);
  if (need == 0 || GetFullPathNameA(path, need + 1, out, NULL) == 0)
    strcpy(out, path);
  return out;
}
#else
TCS_FILES_FN char *tcs_files_full_path(const char *path) {
  char cwd[4096];
  size_t plen = strlen(path);
  int absolute = plen > 0 && path[0] == '/';
  char *joined;
  if (absolute) {
    joined = tcs_files_alloc(plen + 1);
    memcpy(joined, path, plen + 1);
  } else {
    if (getcwd(cwd, sizeof cwd) == NULL)
      cwd[0] = 0;
    size_t clen = strlen(cwd);
    joined = tcs_files_alloc(clen + plen + 2);
    memcpy(joined, cwd, clen);
    joined[clen] = '/';
    memcpy(joined + clen + 1, path, plen + 1);
  }
  size_t n = strlen(joined);
  char *out = tcs_files_alloc(n + 2);
  size_t o = 0;
  size_t i = 0;
  while (i < n) {
    while (i < n && tcs_files_is_sep(joined[i]))
      i++;
    size_t start = i;
    while (i < n && !tcs_files_is_sep(joined[i]))
      i++;
    size_t len = i - start;
    if (len == 0 || (len == 1 && joined[start] == '.'))
      continue;
    if (len == 2 && joined[start] == '.' && joined[start + 1] == '.') {
      while (o > 0 && !tcs_files_is_sep(out[o - 1]))
        o--;
      if (o > 0)
        o--;
      continue;
    }
    out[o++] = '/';
    memcpy(out + o, joined + start, len);
    o += len;
  }
  if (o == 0)
    out[o++] = '/';
  out[o] = 0;
  /* Keep a trailing separator, as .NET does */
  if (plen > 0 && tcs_files_is_sep(path[plen - 1]) &&
      !tcs_files_is_sep(out[o - 1])) {
    out[o++] = path[plen - 1];
    out[o] = 0;
  }
  free(joined);
  return out;
}
#endif

TCS_FILES_FN int tcs_files_mkdirs(const char *dir) {
  struct stat st;
  if (stat(dir, &st) == 0)
    return 0;
  size_t n = strlen(dir);
  char *buf = tcs_files_alloc(n + 1);
  memcpy(buf, dir, n + 1);
  size_t cut = n;
  while (cut > 0 && !tcs_files_is_sep(buf[cut - 1]))
    cut--;
  if (cut > 1) {
    buf[cut - 1] = 0;
    tcs_files_mkdirs(buf);
  }
  free(buf);
#ifdef _WIN32
  return _mkdir(dir);
#else
  return mkdir(dir, 0777);
#endif
}

/* The reason of a failed write (malloc'd) */
TCS_FILES_FN char *tcs_files_error_text(const char *what, const char *path) {
  size_t n = strlen(what) + strlen(path) + 256;
  char *buf = tcs_files_alloc(n);
  snprintf(buf, n, "%s '%s': %s", what, path, strerror(errno));
  return buf;
}

/* Writes to a temporary file, then replaces the file (so partially written
 * output is never read). Makes the directories of the path. Returns NULL on
 * success, or the reason (malloc'd) */
TCS_FILES_FN char *tcs_files_write(const char *path, const char *data,
                                   size_t length) {
  char *full = tcs_files_full_path(path);
  size_t n = strlen(full);
  size_t cut = n;
  while (cut > 0 && !tcs_files_is_sep(full[cut - 1]))
    cut--;
  char *dir = tcs_files_alloc(cut + 1);
  memcpy(dir, full, cut);
  dir[cut > 1 ? cut - 1 : cut] = 0;
  tcs_files_mkdirs(dir);
  char *failure = NULL;
  struct stat st;
  int exists = stat(full, &st) == 0;
  if (exists) {
    FILE *probe = fopen(full, "r+b");
    if (probe == NULL) {
      failure = tcs_files_error_text("cannot write", full);
      free(dir);
      free(full);
      return failure;
    }
    fclose(probe);
  }
  char *tmp = tcs_files_alloc(n + 64);
  snprintf(tmp, n + 64, "%s/.%s.%d.tmp", dir, full + cut,
           (int)tcs_files_getpid());
  FILE *f = fopen(tmp, "wb");
  if (f == NULL)
    failure = tcs_files_error_text("cannot write", tmp);
  else {
    int ok = fwrite(data, 1, length, f) == length;
    ok = fclose(f) == 0 && ok;
#ifndef _WIN32
    if (ok && exists)
      chmod(tmp, st.st_mode & 07777);
#else
    if (ok && exists)
      remove(full);
#endif
    if (!ok || rename(tmp, full) != 0) {
      failure = tcs_files_error_text("cannot write", full);
      remove(tmp);
    }
  }
  free(tmp);
  free(dir);
  free(full);
  return failure;
}

/* A stamp to compare for changes: modification time and size. "" for a path
 * that is not a file */
TCS_FILES_FN void tcs_files_stamp(const char *path, char out[96]) {
  out[0] = 0;
#ifdef _WIN32
  /* The last write time is in 100 ns units (stat's st_mtime is in seconds and
   * misses saves of the same size within the same second) */
  WIN32_FILE_ATTRIBUTE_DATA data;
  int found = GetFileAttributesExA(path, GetFileExInfoStandard, &data) != 0;
  if (!found || (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    return;
  long long ticks = ((long long)data.ftLastWriteTime.dwHighDateTime << 32) |
                    data.ftLastWriteTime.dwLowDateTime;
  long long size = ((long long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
  snprintf(out, 96, "%lld:%lld", ticks, size);
#else
  struct stat st;
  if (stat(path, &st) != 0 || (st.st_mode & S_IFMT) != S_IFREG)
    return;
#if defined(__APPLE__)
  long long ns = (long long)st.st_mtimespec.tv_sec * 1000000000LL +
                 st.st_mtimespec.tv_nsec;
#else
  long long ns =
      (long long)st.st_mtim.tv_sec * 1000000000LL + st.st_mtim.tv_nsec;
#endif
  snprintf(out, 96, "%lld:%lld", ns, (long long)st.st_size);
#endif
}

/* The dir of the files shipped with a program (with the separator after it):
 * the TCS_HOME environment variable, or the dir of the executable (argv0 if
 * the OS does not tell). malloc'd */
TCS_FILES_FN char *tcs_files_home(const char *argv0) {
  const char *env = getenv("TCS_HOME");
  char exe[4096];
  exe[0] = 0;
  if (env != NULL && env[0] != 0) {
    snprintf(exe, sizeof exe, "%s/", env);
  } else {
#if defined(_WIN32)
    DWORD got = GetModuleFileNameA(NULL, exe, sizeof exe);
    if (got == 0 || got >= sizeof exe)
      exe[0] = 0;
#elif defined(__APPLE__)
    uint32_t size = sizeof exe;
    if (_NSGetExecutablePath(exe, &size) != 0)
      exe[0] = 0;
#else
    ssize_t got = readlink("/proc/self/exe", exe, sizeof exe - 1);
    exe[got > 0 ? got : 0] = 0;
#endif
    if (exe[0] == 0 && argv0 != NULL)
      snprintf(exe, sizeof exe, "%s", argv0);
  }
  size_t cut = strlen(exe);
  while (cut > 0 && !tcs_files_is_sep(exe[cut - 1]))
    cut--;
  char *home = tcs_files_alloc(cut + 1);
  memcpy(home, exe, cut);
  home[cut] = 0;
  return home;
}

#endif
