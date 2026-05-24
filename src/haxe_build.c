#include "haxe_build.h"
#include "embedded_prelude.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// ---- small helpers ---------------------------------------------------------

static char *trim_inplace(char *s) {
    while (*s == ' ' || *s == '\t') ++s;
    char *e = s + strlen(s);
    while (e > s && (e[-1] == ' ' || e[-1] == '\t' || e[-1] == '\r' || e[-1] == '\n')) --e;
    *e = '\0';
    return s;
}

// `prefix` で始まり、その後ろが space / tab で続いている (= argument あり) なら
// argument 部分 (trim 済み) を返す。違うなら NULL。
static const char *match_flag(char *line, const char *prefix) {
    size_t n = strlen(prefix);
    if (strncmp(line, prefix, n) != 0) return NULL;
    char c = line[n];
    if (c != ' ' && c != '\t') return NULL;
    char *arg = line + n + 1;
    return trim_inplace(arg);
}

const char *path_basename_noext(const char *path, char *out, size_t outsz) {
    if (!path || !out || outsz == 0) return out;
    const char *slash = strrchr(path, '/');
    const char *bs = strrchr(path, '\\');
    const char *base = path;
    if (slash && slash >= base) base = slash + 1;
    if (bs && bs >= base) base = bs + 1;
    SDL_snprintf(out, outsz, "%s", base);
    char *dot = strrchr(out, '.');
    if (dot) *dot = '\0';
    return out;
}

void path_dirname(const char *path, char *out, size_t outsz) {
    if (!path || !out || outsz == 0) return;
    const char *slash = strrchr(path, '/');
    const char *bs = strrchr(path, '\\');
    const char *cut = slash;
    if (bs && (!cut || bs > cut)) cut = bs;
    if (cut && cut > path) {
        size_t n = (size_t)(cut - path);
        if (n >= outsz) n = outsz - 1;
        memcpy(out, path, n);
        out[n] = '\0';
    } else if (cut == path) {
        // path begins with "/" — dirname is "/"
        SDL_snprintf(out, outsz, "/");
    } else {
        SDL_snprintf(out, outsz, ".");
    }
}

// ---- hxml parser -----------------------------------------------------------

bool hxml_parse(const char *hxml_path, HxmlMeta *out) {
    if (!out) return false;
    SDL_zerop(out);
    if (!hxml_path) return false;

    FILE *f = fopen(hxml_path, "r");
    if (!f) {
        SDL_Log("hxml_parse: cannot open %s", hxml_path);
        return false;
    }
    char line[1024];
    while (fgets(line, sizeof(line), f)) {
        char *t = trim_inplace(line);
        if (!*t || *t == '#') continue;

        const char *arg = NULL;
        if ((arg = match_flag(t, "-main")) != NULL) {
            SDL_snprintf(out->main_class, sizeof(out->main_class), "%s", arg);
        } else if ((arg = match_flag(t, "--class-path")) != NULL ||
                   (arg = match_flag(t, "-cp")) != NULL) {
            if (out->cp_count < 8) {
                SDL_snprintf(out->cp_paths[out->cp_count], sizeof(out->cp_paths[0]),
                             "%s", arg);
                out->cp_count++;
            }
        }
    }
    fclose(f);

    if (!out->main_class[0]) {
        SDL_Log("hxml_parse: -main not found in %s", hxml_path);
        return false;
    }
    return true;
}

// ---- file I/O helpers ------------------------------------------------------

static bool read_file_to_string(const char *path, char **out_buf, size_t *out_len) {
    FILE *f = fopen(path, "rb");
    if (!f) return false;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return false; }
    long n = ftell(f);
    if (n < 0) { fclose(f); return false; }
    if (fseek(f, 0, SEEK_SET) != 0) { fclose(f); return false; }
    char *buf = (char*)SDL_malloc((size_t)n + 1);
    if (!buf) { fclose(f); return false; }
    size_t r = fread(buf, 1, (size_t)n, f);
    fclose(f);
    buf[r] = '\0';
    *out_buf = buf;
    *out_len = r;
    return true;
}

// raw + prelude + "\nreturn <main>\n" を out_path.tmp に書いて rename。
static bool concat_and_atomic_write(const char *raw_path,
                                    const char *out_path,
                                    const char *main_class) {
    char *raw = NULL;
    size_t raw_len = 0;
    if (!read_file_to_string(raw_path, &raw, &raw_len)) {
        SDL_Log("concat: cannot read raw file %s", raw_path);
        return false;
    }

    char tmp_path[768];
    SDL_snprintf(tmp_path, sizeof(tmp_path), "%s.tmp", out_path);
    FILE *f = fopen(tmp_path, "wb");
    if (!f) {
        SDL_Log("concat: cannot open tmp %s", tmp_path);
        SDL_free(raw);
        return false;
    }
    size_t prelude_len = strlen(HAXE_PRELUDE);
    bool write_ok = true;
    if (fwrite(HAXE_PRELUDE, 1, prelude_len, f) != prelude_len) write_ok = false;
    if (write_ok && raw_len > 0 && fwrite(raw, 1, raw_len, f) != raw_len) write_ok = false;
    if (write_ok && fprintf(f, "\nreturn %s\n", main_class) < 0) write_ok = false;
    if (fclose(f) != 0) write_ok = false;
    SDL_free(raw);
    if (!write_ok) {
        SDL_Log("concat: write failed for %s", tmp_path);
        SDL_RemovePath(tmp_path);
        return false;
    }

    if (!SDL_RenamePath(tmp_path, out_path)) {
        SDL_Log("rename %s -> %s failed: %s", tmp_path, out_path, SDL_GetError());
        SDL_RemovePath(tmp_path);
        return false;
    }
    return true;
}

// haxe の stdout (stderr merged) を r->log の末尾 4KB に詰める。
// 既存内容との連結ではなく「最後の 4KB」のロールバッファ。
// 1 回の read で cap (4095) を超えることが無い (buf < cap) ので、
// 「先頭シフトして末尾に append」だけで成立する。
static void capture_process_output(SDL_Process *p, HaxeBuildResult *r) {
    SDL_IOStream *out = SDL_GetProcessOutput(p);
    if (!out) return;
    const size_t cap = sizeof(r->log) - 1;
    char buf[1024];
    char roll[sizeof(r->log)];
    size_t logged = 0;
    roll[0] = '\0';
    for (;;) {
        size_t n = SDL_ReadIO(out, buf, sizeof(buf));
        if (n == 0) break;
        if (n > cap) n = cap;   // 安全側 (sizeof(buf) < cap だが念のため)
        if (logged + n <= cap) {
            memcpy(roll + logged, buf, n);
            logged += n;
        } else {
            size_t shift = (logged + n) - cap;
            if (shift < logged) {
                memmove(roll, roll + shift, logged - shift);
                logged -= shift;
            } else {
                logged = 0;
            }
            memcpy(roll + logged, buf, n);
            logged += n;
        }
        roll[logged] = '\0';
    }
    if (logged > 0) {
        memcpy(r->log, roll, logged);
        r->log[logged] = '\0';
    }
}

// ---- main entry ------------------------------------------------------------

HaxeBuildResult haxe_build_run(const HaxeServer *server,
                               const char *hxml_path,
                               const HxmlMeta *meta) {
    HaxeBuildResult r;
    r.ok = false;
    r.log[0] = '\0';

    if (!server || !hxml_path || !meta) {
        SDL_snprintf(r.log, sizeof(r.log), "haxe_build_run: null arg");
        return r;
    }

    char dir[512];
    path_dirname(hxml_path, dir, sizeof(dir));
    char base[256];
    path_basename_noext(hxml_path, base, sizeof(base));

    // <dir>/.lub/
    char lub_dir[640];
    SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", dir);
    if (!SDL_CreateDirectory(lub_dir)) {
        // 既存ディレクトリでも CreateDirectory は成功するはずだが、念のため
        // 失敗時に log だけ残して続行。後段の rename が失敗すれば結局 fail する。
        SDL_Log("haxe_build_run: SDL_CreateDirectory(%s) failed: %s",
                lub_dir, SDL_GetError());
    }

    char raw_tmp[768];
    SDL_snprintf(raw_tmp, sizeof(raw_tmp), "%s/%s.raw.tmp", lub_dir, base);

    char port_str[16];
    SDL_snprintf(port_str, sizeof(port_str), "%d", server->port);

    const char *argv[] = {
        "haxe",
        "--connect", port_str,
        hxml_path,
        "--lua", raw_tmp,
        NULL
    };

    SDL_PropertiesID props = SDL_CreateProperties();
    if (props == 0) {
        SDL_snprintf(r.log, sizeof(r.log),
                     "SDL_CreateProperties failed: %s", SDL_GetError());
        return r;
    }
    SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER, (void*)argv);
    SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER,
                          SDL_PROCESS_STDIO_APP);
    SDL_SetBooleanProperty(props, SDL_PROP_PROCESS_CREATE_STDERR_TO_STDOUT_BOOLEAN,
                           true);

    SDL_Process *p = SDL_CreateProcessWithProperties(props);
    SDL_DestroyProperties(props);
    if (!p) {
        SDL_snprintf(r.log, sizeof(r.log), "spawn failed: %s", SDL_GetError());
        return r;
    }

    int exit_code = -1;
    SDL_WaitProcess(p, true, &exit_code);
    capture_process_output(p, &r);
    SDL_DestroyProcess(p);

    if (exit_code != 0) {
        // r.log には haxe diagnostic がすでに入っている (or 空)。
        // exit_code を末尾に短く付記する。
        size_t cur = strlen(r.log);
        if (cur + 64 < sizeof(r.log)) {
            SDL_snprintf(r.log + cur, sizeof(r.log) - cur,
                         "\n[haxe exit_code=%d]", exit_code);
        }
        // 失敗時は既存 .lua を温存 (touchしない)。raw_tmp は残骸を消す。
        SDL_RemovePath(raw_tmp);
        return r;
    }

    char out_lua[768];
    SDL_snprintf(out_lua, sizeof(out_lua), "%s/%s.lua", lub_dir, base);
    if (!concat_and_atomic_write(raw_tmp, out_lua, meta->main_class)) {
        // log は concat_and_atomic_write 側で SDL_Log 済み。簡潔に詰める。
        size_t cur = strlen(r.log);
        if (cur + 64 < sizeof(r.log)) {
            SDL_snprintf(r.log + cur, sizeof(r.log) - cur,
                         "\n[concat/atomic write failed]");
        } else {
            SDL_snprintf(r.log, sizeof(r.log), "concat/atomic write failed");
        }
        // raw_tmp は残しておくと debug しやすいので、失敗時は消さない。
        return r;
    }
    // 成功時のみ raw_tmp を掃除 (失敗は致命的でない)。
    SDL_RemovePath(raw_tmp);

    r.ok = true;
    return r;
}
