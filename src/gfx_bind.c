// draw の bindings の写しと、shader の名前への結びつけ (src/gfx_bind.h)。
#include "gfx_bind.h"
#include <stdlib.h>
#include <string.h>

// ------------------------------------------------------------ shader names

// 2 の冪。名前は uniform の member (2 block × 32)、texture 8、buffer 4 までで、
// 表の半分を超えない。
#define NAME_SLOTS 128

typedef struct ShaderName {
  char name[32]; // reflection の名前の写し
  uint32_t hash;
  int32_t len; // 0 = 空き
  int8_t tex;  // refl->texs の最初の index (-1 = 無し)
  int8_t sbuf; // refl->storage_bufs の最初の index (-1 = 無し)
  uint8_t n_members;
  uint8_t block[SGL_MAX_UNIFORM_BLOCKS];  // refl->ubs の index
  uint8_t member[SGL_MAX_UNIFORM_BLOCKS]; // その block の members の index
} ShaderName;

struct ShaderNames {
  ShaderName slots[NAME_SLOTS];
};

static uint32_t name_hash(const char *s, int32_t len) {
  uint32_t h = 2166136261u;
  for (int32_t i = 0; i < len; ++i) {
    h ^= (uint8_t)s[i];
    h *= 16777619u;
  }
  return h;
}

// 名前 s の項目 (無ければ足す)。空の名前は NULL。
static ShaderName *names_add(ShaderNames *t, const char *s) {
  size_t sl = strlen(s);
  if (sl == 0 || sl >= sizeof(t->slots[0].name))
    return NULL;
  int32_t len = (int32_t)sl;
  uint32_t h = name_hash(s, len);
  for (uint32_t i = h;; ++i) {
    ShaderName *n = &t->slots[i & (NAME_SLOTS - 1)];
    if (n->len == 0) {
      memcpy(n->name, s, (size_t)len);
      n->hash = h;
      n->len = len;
      n->tex = -1;
      n->sbuf = -1;
      return n;
    }
    if (n->hash == h && n->len == len && memcmp(n->name, s, (size_t)len) == 0)
      return n;
  }
}

static const ShaderName *names_find(const ShaderNames *t, LubStr name) {
  if (!t || !name.ptr || name.len <= 0)
    return NULL;
  uint32_t h = name_hash(name.ptr, name.len);
  for (uint32_t i = h;; ++i) {
    const ShaderName *n = &t->slots[i & (NAME_SLOTS - 1)];
    if (n->len == 0)
      return NULL;
    if (n->hash == h && n->len == name.len &&
        memcmp(n->name, name.ptr, (size_t)name.len) == 0)
      return n;
  }
}

ShaderNames *shader_names_build(const ShaderReflection *refl) {
  ShaderNames *t = (ShaderNames *)calloc(1, sizeof(*t));
  if (!t)
    return NULL;
  for (int b = 0; b < refl->ub_count && b < SGL_MAX_UNIFORM_BLOCKS; ++b) {
    const ShaderUniformBlock *ub = &refl->ubs[b];
    for (int m = 0; m < ub->member_count && m < SGL_MAX_UB_MEMBERS; ++m) {
      ShaderName *n = names_add(t, ub->members[m].name);
      if (n && n->n_members < SGL_MAX_UNIFORM_BLOCKS) {
        n->block[n->n_members] = (uint8_t)b;
        n->member[n->n_members] = (uint8_t)m;
        n->n_members++;
      }
    }
  }
  // 同じ名前が stage ごとにあれば最初のもの (BindingsDesc の slot と同じ)
  for (int i = 0; i < refl->tex_count && i < SGL_MAX_TEXTURES; ++i) {
    ShaderName *n = names_add(t, refl->texs[i].name);
    if (n && n->tex < 0)
      n->tex = (int8_t)i;
  }
  for (int i = 0; i < refl->storage_buf_count && i < SGL_MAX_STORAGE_BUFS;
       ++i) {
    ShaderName *n = names_add(t, refl->storage_bufs[i].name);
    if (n && n->sbuf < 0)
      n->sbuf = (int8_t)i;
  }
  return t;
}

void shader_names_free(ShaderNames *names) { free(names); }

// ------------------------------------------------------------ bind set

bool bindset_copy(BindSet *s, const LubBinding *b, int32_t n) {
  s->items = NULL;
  s->count = 0;
  s->n_uniforms = 0;
  if (n <= 0)
    return true;
  size_t floats = 0, chars = 0;
  for (int32_t i = 0; i < n; ++i) {
    if (b[i].handle == 0 && b[i].values && b[i].count > 0)
      floats += (size_t)b[i].count;
    if (b[i].name.ptr && b[i].name.len > 0)
      chars += (size_t)b[i].name.len + 1;
  }
  // items (pointer を含むので先頭)、値、名前の順に 1 つの確保に置く
  size_t items_bytes = (size_t)n * sizeof(LubBinding);
  size_t need = items_bytes + floats * sizeof(float) + chars;
  if (need > s->mem_cap) {
    void *m = realloc(s->mem, need);
    if (!m)
      return false;
    s->mem = m;
    s->mem_cap = need;
  }
  LubBinding *items = (LubBinding *)s->mem;
  float *fv = (float *)((char *)s->mem + items_bytes);
  char *cv = (char *)(fv + floats);
  for (int32_t i = 0; i < n; ++i) {
    LubBinding *d = &items[i];
    *d = b[i];
    d->name.ptr = "";
    d->name.len = 0;
    if (b[i].name.ptr && b[i].name.len > 0) {
      memcpy(cv, b[i].name.ptr, (size_t)b[i].name.len);
      cv[b[i].name.len] = '\0';
      d->name.ptr = cv;
      d->name.len = b[i].name.len;
      cv += b[i].name.len + 1;
    }
    d->values = NULL;
    if (b[i].handle == 0) {
      s->n_uniforms++;
      if (b[i].values && b[i].count > 0) {
        memcpy(fv, b[i].values, (size_t)b[i].count * sizeof(float));
        d->values = fv;
        fv += b[i].count;
      }
    }
  }
  s->items = items;
  s->count = n;
  return true;
}

void bindset_clear(BindSet *s) {
  s->items = NULL;
  s->count = 0;
  s->n_uniforms = 0;
}

void bindset_free(BindSet *s) {
  free(s->mem);
  memset(s, 0, sizeof(*s));
}

// ------------------------------------------------------------ bind layout

BindTarget bind_target(const ShaderNames *names, LubStr name) {
  BindTarget t = {-1, -1, false};
  // "indices" は index buffer の予約名 (StructuredBuffer には束縛しない)
  t.indices = name.len == 7 && memcmp(name.ptr, "indices", 7) == 0;
  const ShaderName *n = names_find(names, name);
  if (n) {
    t.tex = n->tex;
    if (!t.indices)
      t.sbuf = n->sbuf;
  }
  return t;
}

int uniform_resolve(const ShaderNames *names, const ShaderReflection *refl,
                    const LubBinding *b, UniformWrite *out) {
  const ShaderName *n = names_find(names, b->name);
  if (!n)
    return 0;
  int k = 0;
  for (int i = 0; i < n->n_members; ++i) {
    const ShaderUniformMember *mem =
        &refl->ubs[n->block[i]].members[n->member[i]];
    int off = mem->offset_floats;
    if (off < 0 || off >= UB_MAX_FLOATS)
      continue;
    int size = mem->comp_count < 0 ? 0 : mem->comp_count;
    if (off + size > UB_MAX_FLOATS)
      size = UB_MAX_FLOATS - off;
    int copy = b->values && b->count > 0 ? b->count : 0;
    if (copy > size)
      copy = size;
    out[k].values = b->values;
    out[k].block = n->block[i];
    out[k].offset = (uint16_t)off;
    out[k].size = (uint16_t)size;
    out[k].copy = (uint16_t)copy;
    k++;
  }
  return k;
}

void uniform_writes_apply(const UniformWrite *w, int32_t n,
                          float (*blocks)[UB_MAX_FLOATS]) {
  for (int32_t i = 0; i < n; ++i) {
    float *d = blocks[w[i].block] + w[i].offset;
    if (w[i].copy > 0)
      memcpy(d, w[i].values, (size_t)w[i].copy * sizeof(float));
    if (w[i].size > w[i].copy)
      memset(d + w[i].copy, 0, (size_t)(w[i].size - w[i].copy) * sizeof(float));
  }
}

bool bind_layout_resolve(BindLayout *l, const BindSet *s, LubHandle shader,
                         uint32_t gen, const ShaderNames *names,
                         const ShaderReflection *refl) {
  l->valid = false;
  l->n_writes = 0;
  if (s->count > l->cap) {
    BindTarget *t = (BindTarget *)realloc(l->targets, (size_t)s->count *
                                                          sizeof(BindTarget));
    if (!t)
      return false;
    l->targets = t;
    UniformWrite *w = (UniformWrite *)realloc(
        l->writes,
        (size_t)s->count * SGL_MAX_UNIFORM_BLOCKS * sizeof(UniformWrite));
    if (!w)
      return false;
    l->writes = w;
    l->cap = s->count;
  }
  for (int32_t i = 0; i < s->count; ++i) {
    const LubBinding *b = &s->items[i];
    if (b->handle == 0) {
      BindTarget none = {-1, -1, false};
      l->targets[i] = none;
      l->n_writes += uniform_resolve(names, refl, b, l->writes + l->n_writes);
    } else {
      l->targets[i] = bind_target(names, b->name);
    }
  }
  l->shader = shader;
  l->gen = gen;
  l->valid = true;
  return true;
}

void bind_layout_free(BindLayout *l) {
  free(l->targets);
  free(l->writes);
  memset(l, 0, sizeof(*l));
}

// ------------------------------------------------------------ draw state

void draw_state_free(DrawState *ds) {
  if (!ds)
    return;
  bindset_free(&ds->fixed);
  bind_layout_free(&ds->layout);
  free(ds);
}
