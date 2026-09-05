// audio の C API。raw PCM だけを受ける core 契約 (docs/roadmap.md)。snd は
// key で宣言し、宣言が途切れたら sweep する。decode は png_load と同格の
// 純関数 utility で snd handle を作らない。
#include "api_internal.h"
#include "audio.h"
#include <stdlib.h>
#include <string.h>

static AudioState *audio_lazy(App *app) {
  if (!app->audio)
    app->audio = audio_state_create();
  return app->audio;
}

// key で宣言する snd の表 (所有権の規則 3)。snd handle 自体は audio.c が
// 内容で dedupe して振るので、複数の key が同じ snd を指しうる。
typedef struct AudioSndEntry {
  char key[128];
  int32_t snd;
  int32_t version;
  bool has_version;
  int64_t last_seen_frame;
} AudioSndEntry;

struct AudioSnds {
  AudioSndEntry *e;
  int n, cap;
};

static struct AudioSnds *snds_lazy(App *app) {
  if (!app->audio_snds)
    app->audio_snds = (struct AudioSnds *)calloc(1, sizeof(struct AudioSnds));
  return app->audio_snds;
}

static AudioSndEntry *snd_entry_find(struct AudioSnds *s, LubStr key) {
  for (int i = 0; i < s->n; ++i)
    if (lub_str_eq(key, s->e[i].key))
      return &s->e[i];
  return NULL;
}

// 他の entry が同じ snd を指していなければ退役させる。
static void snd_release(App *app, struct AudioSnds *s, int32_t snd) {
  for (int i = 0; i < s->n; ++i)
    if (s->e[i].snd == snd)
      return;
  if (app->audio)
    audio_snd_retire(app->audio, snd);
}

LubStatus lub_audio_snd(LubContext *ctx, LubStr key, const float *samples,
                        int32_t count, int32_t channels, int32_t rate,
                        const int32_t *version, int32_t *out_snd) {
  App *app = lub_api_app(ctx);
  AudioState *st = audio_lazy(app);
  struct AudioSnds *s = snds_lazy(app);
  if (!st || !s)
    return lub_api_fail(app, "audio: state create failed");
  int64_t now = (int64_t)app->frame_index;
  AudioSndEntry *e = snd_entry_find(s, key);
  if (e && version && e->has_version && e->version == *version) {
    e->last_seen_frame = now;
    *out_snd = e->snd;
    return LUB_OK;
  }
  if (!samples)
    return LUB_NOT_FOUND; // version が違う (か未宣言) なので samples が要る
  if (count <= 0 || channels <= 0 || count % channels != 0)
    return lub_api_fail(app,
                        "audio_snd: %d samples not divisible by %d channels",
                        count, channels);
  int id = audio_snd_from_pcm(st, samples, (uint32_t)(count / channels),
                              (uint32_t)channels, (uint32_t)rate);
  if (id == 0)
    return lub_api_fail(app, "audio_snd: rejected (registry full or bad args)");
  if (!e) {
    if (s->n >= s->cap) {
      int cap = s->cap ? s->cap * 2 : 16;
      AudioSndEntry *grown =
          (AudioSndEntry *)realloc(s->e, sizeof(AudioSndEntry) * (size_t)cap);
      if (!grown)
        return lub_api_fail(app, "audio_snd: out of memory");
      s->e = grown;
      s->cap = cap;
    }
    e = &s->e[s->n];
    memset(e, 0, sizeof(*e));
    if (!lub_str_copy(key, e->key, sizeof(e->key)))
      return lub_api_fail(app, "audio_snd: key too long");
    s->n++;
  } else if (e->snd != id) {
    int32_t old = e->snd;
    e->snd = id;
    snd_release(app, s, old);
  }
  e->snd = id;
  e->has_version = version != NULL;
  e->version = version ? *version : 0;
  e->last_seen_frame = now;
  *out_snd = id;
  return LUB_OK;
}

void api_audio_frame_end(App *app) {
  struct AudioSnds *s = app->audio_snds;
  if (!s || app->resource_sweep_after_frames <= 0)
    return;
  int64_t cf = (int64_t)app->frame_index;
  int64_t thr = (int64_t)app->resource_sweep_after_frames;
  for (int i = 0; i < s->n;) {
    if (cf - s->e[i].last_seen_frame > thr) {
      int32_t snd = s->e[i].snd;
      s->e[i] = s->e[s->n - 1];
      s->n--;
      snd_release(app, s, snd);
    } else {
      ++i;
    }
  }
}

LubStatus lub_audio_decode(LubContext *ctx, const uint8_t *data, int32_t len,
                           LubView *pcm, int32_t *channels, int32_t *rate) {
  App *app = lub_api_app(ctx);
  if (!data || len <= 0)
    return lub_api_fail(app, "audio_decode: empty data");
  uint32_t frames = 0, ch = 0, r = 0;
  float *out = audio_decode_bytes(data, (size_t)len, &frames, &ch, &r);
  if (!out)
    return lub_api_fail(app, "audio_decode: unsupported or corrupt data");
  app_frame_garbage_push(app, out); // view の実体は frame の終わりに回収
  if (pcm) {
    pcm->ptr = (const uint8_t *)out;
    pcm->len = (int32_t)((size_t)frames * ch * sizeof(float));
    pcm->frame = (int32_t)app->frame_index;
  }
  if (channels)
    *channels = (int32_t)ch;
  if (rate)
    *rate = (int32_t)r;
  return LUB_OK;
}

static void play_desc_defaults(const LubAudioPlayDesc *d,
                               LubAudioPlayDesc *out) {
  if (d) {
    *out = *d;
  } else {
    out->volume = 1.0f;
    out->pitch = 1.0f;
    out->pan = 0.0f;
    out->loop = false;
  }
}

bool lub_audio_play(LubContext *ctx, int32_t snd,
                    const LubAudioPlayDesc *desc) {
  AudioState *st = audio_lazy(lub_api_app(ctx));
  if (!st)
    return false;
  LubAudioPlayDesc d;
  play_desc_defaults(desc, &d);
  return audio_play(st, snd, d.volume, d.pitch, d.pan);
}

bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                     const LubAudioPlayDesc *desc) {
  AudioState *st = audio_lazy(lub_api_app(ctx));
  char kbuf[128];
  if (!st || !lub_str_copy(key, kbuf, sizeof(kbuf)))
    return false;
  LubAudioPlayDesc d;
  play_desc_defaults(desc, &d);
  return audio_voice(st, kbuf, snd, d.loop, d.volume, d.pitch, d.pan);
}

void lub_audio_master_volume(LubContext *ctx, float volume) {
  AudioState *st = audio_lazy(lub_api_app(ctx));
  if (st)
    audio_master_volume(st, volume);
}

void lub_audio_info(LubContext *ctx, LubAudioInfo *out) {
  AudioState *st = audio_lazy(lub_api_app(ctx));
  memset(out, 0, sizeof(*out));
  if (!st)
    return;
  AudioInfo info;
  audio_state_info(st, &info);
  out->device = info.device_ok;
  out->rate = (int32_t)info.rate;
  out->voices = info.active_voices;
  out->snds = info.snds;
}

void api_audio_shutdown(App *app) {
  if (!app->audio_snds)
    return;
  free(app->audio_snds->e);
  free(app->audio_snds);
  app->audio_snds = NULL;
}
