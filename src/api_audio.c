// C API for audio.
// Core contract that accepts only raw PCM.
// A snd is declared by key and swept when its declarations stop.
// decode is a pure-function utility on par with png_load and does not create a
// snd handle.
#include "api_internal.h"
#include "audio.h"
#include <stdlib.h>
#include <string.h>

static AudioState *audio_lazy(App *app) {
  if (!app->audio)
    app->audio = audio_state_create();
  return app->audio;
}

// Table of snds declared by key (ownership rule 3).
// audio.c assigns the snd handles themselves after deduping by content, so
// multiple keys can point to the same snd.
typedef struct AudioSndEntry {
  char key[AUDIO_KEY_MAX];
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

// Retire it if no other entry points to the same snd.
static void snd_release(App *app, struct AudioSnds *s, int32_t snd) {
  for (int i = 0; i < s->n; ++i)
    if (s->e[i].snd == snd)
      return;
  if (app->audio)
    audio_snd_retire(app->audio, snd);
}

LubStatus lub_audio_snd(LubContext *ctx, LubStr key, const float *samples,
                        int32_t count, int32_t channels, int32_t rate,
                        const int32_t *version, int32_t *out_snd, bool *has) {
  App *app = lub_api_app(ctx);
  *has = false;
  AudioState *st = audio_lazy(app);
  struct AudioSnds *s = snds_lazy(app);
  if (!st || !s)
    return lub_api_fail(app, "audio: state create failed");
  int64_t now = app->resource_frame;
  AudioSndEntry *e = snd_entry_find(s, key);
  if (e && version && e->has_version && e->version == *version) {
    e->last_seen_frame = now;
    *out_snd = e->snd;
    *has = true;
    return LUB_OK;
  }
  // A query whose version did not match / a declaration without data
  if (!samples && (version || count == LUB_DATA_DEFERRED))
    return LUB_NOT_FOUND;
  if (!samples)
    return lub_api_fail(
        app, "audio_snd: data is required unless the version is given");
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
  *has = true;
  return LUB_OK;
}

void api_audio_frame_end(App *app) {
  struct AudioSnds *s = app->audio_snds;
  if (!s || app->resource_sweep_after_frames <= 0)
    return;
  int64_t cf = app->resource_frame;
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

// Declare with a byte array of f32 PCM (SndBytes in the stub).
LubStatus lub_audio_snd_bytes(LubContext *ctx, LubStr key, const uint8_t *data,
                              int32_t data_len, int32_t channels, int32_t rate,
                              const int32_t *version, int32_t *out, bool *has) {
  App *app = lub_api_app(ctx);
  // If the version matches, the data (and its length) is not looked at
  if (version) {
    LubStatus st = lub_audio_snd(ctx, key, NULL, LUB_DATA_DEFERRED, channels,
                                 rate, version, out, has);
    if (st != LUB_NOT_FOUND)
      return st;
  }
  if (!data)
    return version || data_len == LUB_DATA_DEFERRED
               ? LUB_NOT_FOUND
               : lub_api_fail(
                     app,
                     "audio_snd: data is required unless the version is given");
  if (data_len % (int32_t)sizeof(float) != 0)
    return lub_api_fail(app, "audio_snd: byte length %d is not f32-aligned",
                        data_len);
  return lub_audio_snd(ctx, key, (const float *)data,
                       data_len / (int32_t)sizeof(float), channels, rate,
                       version, out, has);
}

LubStatus lub_audio_decode(LubContext *ctx, const uint8_t *data, int32_t len,
                           LubView *pcm, int32_t *channels, int32_t *rate) {
  App *app = lub_api_app(ctx);
  if (!data || len <= 0)
    return lub_api_fail(app, "audio_decode: empty data");
  uint32_t frames = 0, ch = 0, r = 0;
  float *out = audio_decode_bytes(data, (size_t)len, &frames, &ch, &r);
  if (!out)
    return LUB_NOT_FOUND; // Unsupported / corrupt data returns nil
  app_frame_garbage_push(
      app, out); // The view's backing data is reclaimed at the end of the frame
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

static void play_opts(const LubPlayOpts *o, float *volume, float *pitch,
                      float *pan) {
  *volume = o && o->has_volume ? o->volume : 1.0f;
  *pitch = o && o->has_pitch ? o->pitch : 1.0f;
  *pan = o && o->has_pan ? o->pan : 0.0f;
}

bool lub_audio_play(LubContext *ctx, int32_t snd, const LubPlayOpts *opts) {
  if (lub_api_app(ctx)->digest.enabled) {
    digest_tag(lub_api_app(ctx), "audio_play");
    digest_i32(lub_api_app(ctx), snd);
  }
  // Do not play while the same frame is being recreated from recorded input
  if (lub_api_app(ctx)->replaying)
    return false;
  AudioState *st = audio_lazy(lub_api_app(ctx));
  if (!st)
    return false;
  float volume, pitch, pan;
  play_opts(opts, &volume, &pitch, &pan);
  return audio_play(st, snd, volume, pitch, pan);
}

bool lub_audio_voice(LubContext *ctx, LubStr key, int32_t snd,
                     const LubVoiceOpts *opts) {
  App *app = lub_api_app(ctx);
  if (app->replaying)
    return false;
  char kbuf[AUDIO_KEY_MAX];
  if (!lub_str_copy(key, kbuf, sizeof(kbuf))) {
    // Voice is an API that does not throw errors, so instead of truncating and
    // confusing it with another key, do not play and write to the log.
    SDL_Log("audio_voice: key too long (max %d bytes)", AUDIO_KEY_MAX - 1);
    return false;
  }
  AudioState *st = audio_lazy(app);
  if (!st)
    return false;
  float volume, pitch, pan;
  // VoiceOpts is the PlayOpts fields plus loop (the stub has no inheritance)
  LubPlayOpts play = {0};
  if (opts) {
    play.has_volume = opts->has_volume;
    play.volume = opts->volume;
    play.has_pitch = opts->has_pitch;
    play.pitch = opts->pitch;
    play.has_pan = opts->has_pan;
    play.pan = opts->pan;
  }
  play_opts(opts ? &play : NULL, &volume, &pitch, &pan);
  bool loop = opts && opts->has_loop && opts->loop;
  return audio_voice(st, kbuf, snd, loop, volume, pitch, pan);
}

void lub_audio_master_volume(LubContext *ctx, float volume) {
  // While the same frame is being recreated from recorded input, do not change
  // the sound state
  if (lub_api_app(ctx)->replaying)
    return;
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
