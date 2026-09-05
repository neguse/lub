// audio の C API。raw PCM だけを受ける core 契約 (docs/roadmap.md)。decode
// は png_load と同格の純関数 utility で snd handle を作らない。
#include "api_internal.h"
#include "audio.h"
#include <stdlib.h>
#include <string.h>

static AudioState *audio_lazy(App *app) {
  if (!app->audio)
    app->audio = audio_state_create();
  return app->audio;
}

LubStatus lub_audio_pcm(LubContext *ctx, const float *samples, int32_t count,
                        int32_t channels, int32_t rate, int32_t *out_snd) {
  App *app = lub_api_app(ctx);
  AudioState *st = audio_lazy(app);
  if (!st)
    return lub_api_fail(app, "audio: state create failed");
  if (!samples || count <= 0 || channels <= 0 || count % channels != 0)
    return lub_api_fail(app,
                        "audio_pcm: %d samples not divisible by %d channels",
                        count, channels);
  int id = audio_snd_from_pcm(st, samples, (uint32_t)(count / channels),
                              (uint32_t)channels, (uint32_t)rate);
  if (id == 0)
    return lub_api_fail(app, "audio_pcm: rejected (registry full or bad args)");
  *out_snd = id;
  return LUB_OK;
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
  // 直前の decode 結果 (view の実体) は次の decode で解放する。
  free(app->audio_decode_view);
  app->audio_decode_view = out;
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

bool lub_audio_free(LubContext *ctx, int32_t snd) {
  AudioState *st = audio_lazy(lub_api_app(ctx));
  return st && audio_snd_free(st, snd);
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
  free(app->audio_decode_view);
  app->audio_decode_view = NULL;
}
