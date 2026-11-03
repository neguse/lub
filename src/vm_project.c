// tcs VM の player の .csproj の入力 (vm_project.h)。
#include "vm_project.h"
#include "path_util.h"
#include "tcs_build.h"
#include <string.h>

static bool file_exists(const char *path) {
  SDL_PathInfo info;
  return SDL_GetPathInfo(path, &info) && info.type == SDL_PATHTYPE_FILE;
}

static bool str_ends_with(const char *s, const char *suffix) {
  size_t ls = SDL_strlen(s), lf = SDL_strlen(suffix);
  return ls >= lf && SDL_strcmp(s + ls - lf, suffix) == 0;
}

// 区切りを '/' に揃え、"." と ".." の段を畳む。
// 先頭の ".." は畳めないので残す。
static void path_normalize(const char *in, char *out, size_t outsz) {
  char buf[1024];
  SDL_strlcpy(buf, in, sizeof(buf));
  for (char *c = buf; *c; c++)
    if (*c == '\\')
      *c = '/';
  bool absolute = buf[0] == '/';
  const char *seg[128];
  int n = 0;
  char *save = NULL;
  for (char *tok = SDL_strtok_r(buf, "/", &save); tok;
       tok = SDL_strtok_r(NULL, "/", &save)) {
    if (SDL_strcmp(tok, ".") == 0)
      continue;
    if (SDL_strcmp(tok, "..") == 0 && n > 0 && SDL_strcmp(seg[n - 1], "..")) {
      n--;
      continue;
    }
    if (n < 128)
      seg[n++] = tok;
  }
  size_t len = 0;
  out[0] = '\0';
  if (absolute)
    len = SDL_strlcpy(out, "/", outsz);
  for (int i = 0; i < n && len < outsz; i++)
    len += SDL_snprintf(out + len, outsz - len, "%s%s", i ? "/" : "", seg[i]);
  if (n == 0 && !absolute)
    SDL_strlcpy(out, ".", outsz);
}

static bool path_is_absolute(const char *p) {
  return p[0] == '/' || p[0] == '\\' || (p[0] && p[1] == ':');
}

// 相対 path は cwd に繋いでから畳む (同じ file を書き方によらず比べるため)
static void path_absolute(const char *in, char *out, size_t outsz) {
  char joined[1400];
  char *cwd = path_is_absolute(in) ? NULL : SDL_GetCurrentDirectory();
  SDL_snprintf(joined, sizeof(joined), "%s%s", cwd ? cwd : "", in);
  SDL_free(cwd);
  path_normalize(joined, out, outsz);
}

// tag (要素名の直後から '>' の手前まで) から属性 name の値を out に書く。
static bool xml_attr(const char *tag, size_t len, const char *name, char *out,
                     size_t outsz) {
  size_t name_len = SDL_strlen(name);
  for (size_t i = 1; i + name_len < len; i++) {
    char before = tag[i - 1];
    if ((before != ' ' && before != '\t' && before != '\r' && before != '\n') ||
        SDL_strncmp(tag + i, name, name_len) != 0)
      continue;
    size_t j = i + name_len;
    while (j < len && (tag[j] == ' ' || tag[j] == '\t'))
      j++;
    if (j >= len || tag[j] != '=')
      continue;
    j++;
    while (j < len && (tag[j] == ' ' || tag[j] == '\t'))
      j++;
    if (j >= len || (tag[j] != '"' && tag[j] != '\''))
      continue;
    char quote = tag[j++];
    size_t start = j;
    while (j < len && tag[j] != quote)
      j++;
    if (j >= len || j - start >= outsz)
      return false;
    SDL_memcpy(out, tag + start, j - start);
    out[j - start] = '\0';
    return true;
  }
  return false;
}

static bool add_dir(VmProject *proj, const char *dir) {
  for (int i = 0; i < proj->ndirs; i++)
    if (SDL_strcmp(proj->dirs[i], dir) == 0)
      return false;
  if (proj->ndirs >= VM_PROJECT_MAX_DIRS) {
    SDL_Log("lub: too many referenced projects; ignoring %s", dir);
    return false;
  }
  SDL_strlcpy(proj->dirs[proj->ndirs++], dir, sizeof(proj->dirs[0]));
  return true;
}

// csproj の印の付いた ProjectReference を辿る (参照先の参照も、同じ project は 1 回だけ)
static bool collect_refs(VmProject *proj, const char *csproj, int depth) {
  if (depth > 16) // 参照の循環で止まらなくなるのを避ける
    return true;
  size_t len = 0;
  char *text = (char *)SDL_LoadFile(csproj, &len);
  if (!text)
    return true;
  char dir[512];
  path_dirname(csproj, dir, sizeof(dir));
  bool ok = true;
  static const char TAG[] = "<ProjectReference";
  const char *p = text;
  while ((p = SDL_strstr(p, TAG)) != NULL) {
    // comment (<!-- ... -->) の中の要素は読まない
    const char *comment = SDL_strstr(text, "<!--");
    bool commented = false;
    while (comment && comment < p) {
      const char *end = SDL_strstr(comment + 4, "-->");
      if (!end || end > p) {
        commented = true;
        p = end ? end + 3 : text + len;
        break;
      }
      comment = SDL_strstr(end + 3, "<!--");
    }
    if (commented)
      continue;
    p += sizeof(TAG) - 1;
    const char *close = SDL_strchr(p, '>');
    if (!close)
      break;
    size_t tag_len = (size_t)(close - p);
    char role[32], include[512];
    bool source = false, overlay = false;
    if (xml_attr(p, tag_len, "Lub", role, sizeof(role))) {
      source = SDL_strcasecmp(role, "Source") == 0;
      overlay = SDL_strcasecmp(role, "Overlay") == 0;
      if (!source && !overlay)
        SDL_Log("%s: unknown Lub=\"%s\" on a ProjectReference (use Source or "
                "Overlay)",
                csproj, role);
    }
    if ((source || overlay) &&
        xml_attr(p, tag_len, "Include", include, sizeof(include))) {
      if (SDL_strstr(include, "$(")) {
        SDL_Log("%s: skipping ProjectReference %s (lub does not expand "
                "MSBuild properties)",
                csproj, include);
      } else {
        char joined[1100], norm[512], ref_dir[512];
        if (path_is_absolute(include))
          SDL_strlcpy(joined, include, sizeof(joined));
        else
          SDL_snprintf(joined, sizeof(joined), "%s/%s", dir, include);
        path_normalize(joined, norm, sizeof(norm));
        if (!file_exists(norm)) {
          SDL_Log("%s: ProjectReference %s does not exist (%s)", csproj,
                  include, norm);
          ok = false;
        } else {
          if (overlay) {
            char base[256];
            path_basename_noext(norm, base, sizeof(base));
            if (proj->overlay[0] && SDL_strcmp(proj->overlay, base) != 0)
              SDL_Log("lub: only one overlay is supported; ignoring %s", base);
            else
              SDL_strlcpy(proj->overlay, base, sizeof(proj->overlay));
          }
          path_dirname(norm, ref_dir, sizeof(ref_dir));
          char abs_dir[512];
          path_absolute(ref_dir, abs_dir, sizeof(abs_dir));
          if (overlay && !proj->overlay_dir[0])
            SDL_strlcpy(proj->overlay_dir, abs_dir, sizeof(proj->overlay_dir));
          if (add_dir(proj, abs_dir))
            ok = collect_refs(proj, norm, depth + 1) && ok;
        }
      }
    }
    p = close;
  }
  SDL_free(text);
  return ok;
}

bool vm_project_open(VmProject *proj, const char *csproj) {
  SDL_zerop(proj);
  path_basename_noext(csproj, proj->entry, sizeof(proj->entry));
  char dir[512], abs_dir[512];
  path_dirname(csproj, dir, sizeof(dir));
  path_absolute(dir, abs_dir, sizeof(abs_dir));
  add_dir(proj, abs_dir);
  char lub_dir[640];
  SDL_snprintf(lub_dir, sizeof(lub_dir), "%s/.lub", abs_dir);
  SDL_CreateDirectory(lub_dir);
  SDL_snprintf(proj->out_tcsb, sizeof(proj->out_tcsb), "%s/%s.tcsb", lub_dir,
               proj->entry);
  SDL_snprintf(proj->list_path, sizeof(proj->list_path), "%s/%s.inputs",
               lub_dir, proj->entry);
  if (tcs_resolve_cs_lib(proj->cs_lib, sizeof(proj->cs_lib))) {
    char abs_lib[900];
    path_absolute(proj->cs_lib, abs_lib, sizeof(abs_lib));
    SDL_strlcpy(proj->cs_lib, abs_lib, sizeof(proj->cs_lib));
    SDL_snprintf(proj->stub, sizeof(proj->stub), "%s/lub_stub.cs",
                 proj->cs_lib);
    if (!file_exists(proj->stub))
      proj->stub[0] = '\0';
  }
  if (!proj->stub[0])
    SDL_Log("cs-lib/lub_stub.cs not found (set LUB_CS_LIB to the cs-lib "
            "directory); compiling without lub API stub");
  char abs_csproj[640];
  path_absolute(csproj, abs_csproj, sizeof(abs_csproj));
  return collect_refs(proj, abs_csproj, 0);
}

typedef struct TextBuf {
  char *p;
  size_t len, cap;
} TextBuf;

static void text_add(TextBuf *b, const char *s) {
  size_t n = SDL_strlen(s);
  if (b->len + n + 2 > b->cap) {
    size_t cap = b->cap ? b->cap * 2 : 4096;
    while (cap < b->len + n + 2)
      cap *= 2;
    char *grown = (char *)SDL_realloc(b->p, cap);
    if (!grown)
      return;
    b->p = grown;
    b->cap = cap;
  }
  SDL_memcpy(b->p + b->len, s, n);
  b->len += n;
  b->p[b->len++] = '\n';
  b->p[b->len] = '\0';
}

// dir の下の *.cs を、*.ref.cs は refs に、それ以外は inputs に足す
static void scan_dir(const char *dir, TextBuf *inputs, TextBuf *refs,
                     bool skip_stub) {
  int count = 0;
  char **found = tcs_glob_sources(dir, &count);
  for (int i = 0; i < count; i++) {
    char full[1400];
    SDL_snprintf(full, sizeof(full), "%s/%s", dir, found[i]);
    const char *base = SDL_strrchr(found[i], '/');
    base = base ? base + 1 : found[i];
    if (skip_stub && SDL_strcmp(base, "lub_stub.cs") == 0)
      continue;
    text_add(str_ends_with(found[i], ".ref.cs") ? refs : inputs, full);
  }
  tcs_free_sources(found, count);
}

bool vm_project_scan(VmProject *proj) {
  TextBuf inputs = {0}, refs = {0};
  for (int i = 0; i < proj->ndirs; i++)
    scan_dir(proj->dirs[i], &inputs, &refs, false);
  if (proj->cs_lib[0])
    scan_dir(proj->cs_lib, &inputs, &refs, true);
  bool refs_changed = !proj->refs_text ||
                      SDL_strcmp(proj->refs_text, refs.p ? refs.p : "") != 0;
  if (refs_changed && proj->refs_text)
    SDL_Log("lub: the set of *.ref.cs changed; restart lub to use it");
  if (!proj->refs_text) {
    proj->refs_text = refs.p ? refs.p : SDL_strdup("");
    refs.p = NULL;
  }
  SDL_free(refs.p);
  const char *text = inputs.p ? inputs.p : "";
  bool changed = !proj->list_text || SDL_strcmp(proj->list_text, text) != 0;
  if (changed) {
    SDL_IOStream *io = SDL_IOFromFile(proj->list_path, "wb");
    if (io) {
      SDL_WriteIO(io, text, SDL_strlen(text));
      SDL_CloseIO(io);
    } else {
      SDL_Log("lub: cannot write %s: %s", proj->list_path, SDL_GetError());
    }
    SDL_free(proj->list_text);
    proj->list_text = SDL_strdup(text);
  }
  SDL_free(inputs.p);
  return changed;
}

int vm_project_args(VmProject *proj, const char **args, int max) {
  static char exports[2][320];
  int n = 0;
  args[n++] = "--inputs-from";
  args[n++] = proj->list_path;
  if (proj->stub[0]) {
    args[n++] = "--ref";
    args[n++] = proj->stub;
  }
  // *.ref.cs (改行区切りの refs_text を写して区切り、それを指す)
  SDL_free(proj->refs_split);
  proj->refs_split = SDL_strdup(proj->refs_text ? proj->refs_text : "");
  for (char *line = proj->refs_split; line && *line && n < max - 10;) {
    char *nl = SDL_strchr(line, '\n');
    if (nl)
      *nl = '\0';
    args[n++] = "--ref";
    args[n++] = line;
    if (!nl)
      break;
    line = nl + 1;
  }
  args[n++] = "-o";
  args[n++] = proj->out_tcsb;
  SDL_snprintf(exports[0], sizeof(exports[0]), "%s.*", proj->entry);
  args[n++] = "--export";
  args[n++] = exports[0];
  if (proj->overlay[0]) {
    SDL_snprintf(exports[1], sizeof(exports[1]), "%s.*", proj->overlay);
    args[n++] = "--export";
    args[n++] = exports[1];
    // ゲームのソースを --trace にする (overlay のディレクトリは除く。entry と同じなら entry も除かれる)
    for (int i = 0; i < proj->ndirs && n < max - 2; i++) {
      if (SDL_strcmp(proj->dirs[i], proj->overlay_dir) == 0)
        continue;
      args[n++] = "--trace";
      args[n++] = proj->dirs[i];
    }
  }
  return n;
}

void vm_project_close(VmProject *proj) {
  SDL_free(proj->list_text);
  SDL_free(proj->refs_text);
  SDL_free(proj->refs_split);
  proj->list_text = proj->refs_text = proj->refs_split = NULL;
}
