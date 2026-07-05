#include "audio.h"

#include <SDL3/SDL.h>
#include <math.h>
#include <stdlib.h>
#include <string.h>

#define MA_NO_ENGINE
#define MA_NO_NODE_GRAPH
#define MA_NO_RESOURCE_MANAGER
#define MA_NO_GENERATION
#include "miniaudio.h"

#define AUDIO_MAX_SNDS 256
#define AUDIO_MAX_VOICES 64
#define AUDIO_KEY_MAX 64

// voice state machine。FREE→CLAIMED→PLAYING/STOPPING は game thread、
// PLAYING/STOPPING→FREE は audio thread だけが行う。CLAIMED の間は
// audio thread が触らないので、game thread はロック無しで初期化できる。
enum {
  V_FREE = 0,
  V_CLAIMED,
  V_PLAYING,
  V_STOPPING,
};

typedef struct Snd {
  float *pcm; // interleaved f32、game thread が確保し frame_end で回収
  uint32_t frames;
  uint32_t channels; // 1 or 2
  uint32_t rate;
  uint64_t hash; // 内容 dedupe 用 (bytes + channels + rate)
  uint32_t gen;  // handle の世代。free で進めて stale handle を弾く
  bool in_use;
  SDL_AtomicInt alive; // 0 = freed。audio thread は参照 voice を落とす
  SDL_AtomicInt refs;  // attach されている voice 数
} Snd;

typedef struct Voice {
  SDL_AtomicInt state;
  int snd_idx;
  uint32_t snd_gen;
  uint32_t spawn_gen; // slot 再利用の識別 (game thread only)
  bool loop;
  // targets: game thread が書き audio thread が読む (float bits)
  SDL_AtomicU32 t_vol, t_pitch, t_pan;
  // audio thread owned (CLAIMED の間だけ game thread が初期化する)
  double pos; // frames 単位の再生位置
  float c_vol, c_pitch, c_pan;
} Voice;

// declared voice の game 側台帳。「同じ key の宣言が続く限り同じ voice」と
// 「自然終了した key は宣言が途切れるまで再発火しない」を守る。
typedef struct Declared {
  bool in_use;
  bool ended; // voice が自然終了した。key が消えるまで tombstone
  char key[AUDIO_KEY_MAX];
  int snd_id;
  int voice_idx;
  uint32_t voice_gen;
  uint64_t declared_frame;
} Declared;

struct AudioState {
  Snd snds[AUDIO_MAX_SNDS];
  Voice voices[AUDIO_MAX_VOICES];
  Declared declared[AUDIO_MAX_VOICES];
  uint64_t frame;

  bool device_tried;
  bool device_ok;
  ma_context context;
  bool context_inited;
  ma_device device;
  float k_vol, k_pitch; // per-sample one-pole 係数 (tau 5ms / 20ms)
  SDL_AtomicU32 master; // float bits
};

static uint32_t f2b(float f) {
  uint32_t b;
  memcpy(&b, &f, sizeof(b));
  return b;
}

static float b2f(uint32_t b) {
  float f;
  memcpy(&f, &b, sizeof(f));
  return f;
}

static uint64_t fnv1a64(const void *data, size_t len, uint64_t h) {
  const uint8_t *p = (const uint8_t *)data;
  for (size_t i = 0; i < len; i++) {
    h ^= p[i];
    h *= 1099511628211ULL;
  }
  return h;
}

// ---------------------------------------------------------------------------
// mixer (audio thread)

static void voice_detach(Voice *v, Snd *snd) {
  SDL_AddAtomicInt(&snd->refs, -1);
  SDL_SetAtomicInt(&v->state, V_FREE);
}

static void audio_data_callback(ma_device *dev, void *out_v, const void *in_v,
                                ma_uint32 nframes) {
  (void)in_v;
  AudioState *st = (AudioState *)dev->pUserData;
  float *out = (float *)out_v;
  memset(out, 0, (size_t)nframes * 2 * sizeof(float));
  const float master = b2f(SDL_GetAtomicU32(&st->master));
  const float k_vol = st->k_vol;
  const float k_pitch = st->k_pitch;
  const double dev_rate = dev->sampleRate ? (double)dev->sampleRate : 48000.0;

  for (int vi = 0; vi < AUDIO_MAX_VOICES; vi++) {
    Voice *v = &st->voices[vi];
    int state = SDL_GetAtomicInt(&v->state);
    if (state != V_PLAYING && state != V_STOPPING)
      continue;
    Snd *snd = &st->snds[v->snd_idx];
    if (!SDL_GetAtomicInt(&snd->alive) || snd->gen != v->snd_gen) {
      voice_detach(v, snd);
      continue;
    }
    const float *pcm = snd->pcm;
    const double sf = (double)snd->frames;
    const uint32_t ch = snd->channels;
    const double step_scale = (double)snd->rate / dev_rate;
    const float t_vol =
        state == V_STOPPING ? 0.0f : b2f(SDL_GetAtomicU32(&v->t_vol));
    const float t_pitch = b2f(SDL_GetAtomicU32(&v->t_pitch));
    const float t_pan = b2f(SDL_GetAtomicU32(&v->t_pan));

    double pos = v->pos;
    float c_vol = v->c_vol, c_pitch = v->c_pitch, c_pan = v->c_pan;
    bool ended = false;

    for (ma_uint32 i = 0; i < nframes; i++) {
      c_vol += (t_vol - c_vol) * k_vol;
      c_pitch += (t_pitch - c_pitch) * k_pitch;
      c_pan += (t_pan - c_pan) * k_vol;

      if (v->loop) {
        pos = fmod(pos, sf);
        if (pos < 0.0)
          pos += sf;
      } else if (pos < 0.0 || pos >= sf - 1.0) {
        ended = true;
        break;
      }
      uint32_t i0 = (uint32_t)pos;
      float fr = (float)(pos - (double)i0);
      uint32_t i1 = i0 + 1;
      if (i1 >= snd->frames)
        i1 = v->loop ? 0 : i0;
      float l, r;
      if (ch == 1) {
        float s = pcm[i0] + (pcm[i1] - pcm[i0]) * fr;
        l = r = s;
      } else {
        l = pcm[i0 * 2] + (pcm[i1 * 2] - pcm[i0 * 2]) * fr;
        r = pcm[i0 * 2 + 1] + (pcm[i1 * 2 + 1] - pcm[i0 * 2 + 1]) * fr;
      }
      float gl = c_pan > 0.0f ? 1.0f - c_pan : 1.0f;
      float gr = c_pan < 0.0f ? 1.0f + c_pan : 1.0f;
      float g = c_vol * master;
      out[i * 2] += l * g * gl;
      out[i * 2 + 1] += r * g * gr;

      pos += (double)c_pitch * step_scale;

      if (state == V_STOPPING && c_vol < 0.0002f) {
        ended = true;
        break;
      }
    }

    v->pos = pos;
    v->c_vol = c_vol;
    v->c_pitch = c_pitch;
    v->c_pan = c_pan;
    if (ended)
      voice_detach(v, snd);
  }

  for (ma_uint32 i = 0; i < nframes * 2; i++) {
    float s = out[i];
    out[i] = s < -1.0f ? -1.0f : (s > 1.0f ? 1.0f : s);
  }
}

// ---------------------------------------------------------------------------
// device (game thread, lazy)

static void audio_ensure_device(AudioState *st) {
  if (st->device_tried)
    return;
  st->device_tried = true;

  ma_device_config cfg = ma_device_config_init(ma_device_type_playback);
  cfg.playback.format = ma_format_f32;
  cfg.playback.channels = 2;
  cfg.sampleRate = 0; // device native
  cfg.dataCallback = audio_data_callback;
  cfg.pUserData = st;

  ma_result res = ma_device_init(NULL, &cfg, &st->device);
  if (res != MA_SUCCESS) {
    // headless / CI: null backend にフォールバックして mixer は回し続ける
    ma_backend null_backend = ma_backend_null;
    if (ma_context_init(&null_backend, 1, NULL, &st->context) == MA_SUCCESS) {
      st->context_inited = true;
      res = ma_device_init(&st->context, &cfg, &st->device);
    }
  }
  if (res != MA_SUCCESS) {
    SDL_Log("audio: device init failed (%d), audio disabled", (int)res);
    return;
  }
  if (ma_device_start(&st->device) != MA_SUCCESS) {
    SDL_Log("audio: device start failed, audio disabled");
    ma_device_uninit(&st->device);
    return;
  }
  uint32_t rate = st->device.sampleRate;
  st->k_vol = 1.0f - expf(-1.0f / (0.005f * (float)rate));
  st->k_pitch = 1.0f - expf(-1.0f / (0.020f * (float)rate));
  st->device_ok = true;
  SDL_Log("audio: device OK (%s, %u Hz)",
          ma_get_backend_name(st->device.pContext->backend), rate);
}

// ---------------------------------------------------------------------------
// snd registry (game thread)

static Snd *snd_lookup(AudioState *st, int id) {
  if (id < 1)
    return NULL;
  int idx = (id - 1) % AUDIO_MAX_SNDS;
  uint32_t gen = (uint32_t)((id - 1) / AUDIO_MAX_SNDS);
  Snd *snd = &st->snds[idx];
  if (!snd->in_use || snd->gen != gen || !SDL_GetAtomicInt(&snd->alive))
    return NULL;
  return snd;
}

static int snd_id(AudioState *st, Snd *snd) {
  return (int)(snd - st->snds) + 1 + (int)snd->gen * AUDIO_MAX_SNDS;
}

int audio_snd_from_pcm(AudioState *st, const float *interleaved,
                       uint32_t frames, uint32_t channels, uint32_t rate) {
  if (!interleaved || frames < 2 || (channels != 1 && channels != 2) ||
      rate < 1000 || rate > 384000)
    return 0;
  size_t bytes = (size_t)frames * channels * sizeof(float);
  uint64_t h = fnv1a64(interleaved, bytes, 1469598103934665603ULL);
  h = fnv1a64(&channels, sizeof(channels), h);
  h = fnv1a64(&rate, sizeof(rate), h);

  Snd *free_slot = NULL;
  for (int i = 0; i < AUDIO_MAX_SNDS; i++) {
    Snd *snd = &st->snds[i];
    if (!snd->in_use) {
      if (!free_slot)
        free_slot = snd;
      continue;
    }
    if (SDL_GetAtomicInt(&snd->alive) && snd->hash == h &&
        snd->frames == frames && snd->channels == channels &&
        snd->rate == rate && memcmp(snd->pcm, interleaved, bytes) == 0)
      return snd_id(st, snd); // 内容一致 → 同じ handle (hot reload 継続の要)
  }
  if (!free_slot) {
    SDL_Log("audio: snd registry full (%d)", AUDIO_MAX_SNDS);
    return 0;
  }
  float *copy = (float *)malloc(bytes);
  if (!copy)
    return 0;
  memcpy(copy, interleaved, bytes);
  free_slot->pcm = copy;
  free_slot->frames = frames;
  free_slot->channels = channels;
  free_slot->rate = rate;
  free_slot->hash = h;
  free_slot->in_use = true;
  SDL_SetAtomicInt(&free_slot->refs, 0);
  SDL_SetAtomicInt(&free_slot->alive, 1);
  return snd_id(st, free_slot);
}

bool audio_snd_free(AudioState *st, int id) {
  Snd *snd = snd_lookup(st, id);
  if (!snd)
    return false;
  SDL_SetAtomicInt(&snd->alive, 0);
  snd->gen++; // 以後の handle lookup を無効化。PCM 回収は frame_end で
  return true;
}

// ---------------------------------------------------------------------------
// voices (game thread)

static Voice *voice_claim(AudioState *st, int *out_idx) {
  for (int i = 0; i < AUDIO_MAX_VOICES; i++) {
    Voice *v = &st->voices[i];
    if (SDL_CompareAndSwapAtomicInt(&v->state, V_FREE, V_CLAIMED)) {
      if (out_idx)
        *out_idx = i;
      return v;
    }
  }
  return NULL;
}

static bool voice_spawn(AudioState *st, Snd *snd, bool loop, float volume,
                        float pitch, float pan, int *out_idx,
                        uint32_t *out_gen) {
  int idx = 0;
  Voice *v = voice_claim(st, &idx);
  if (!v) {
    SDL_Log("audio: voice pool full (%d)", AUDIO_MAX_VOICES);
    return false;
  }
  v->snd_idx = (int)(snd - st->snds);
  v->snd_gen = snd->gen;
  v->spawn_gen++;
  v->loop = loop;
  SDL_SetAtomicU32(&v->t_vol, f2b(volume));
  SDL_SetAtomicU32(&v->t_pitch, f2b(pitch));
  SDL_SetAtomicU32(&v->t_pan, f2b(pan));
  v->pos = pitch < 0.0f ? (double)snd->frames - 1.001 : 0.0;
  v->c_vol = 0.0f; // 立ち上がりは ramp でクリックを防ぐ
  v->c_pitch = pitch;
  v->c_pan = pan;
  SDL_AddAtomicInt(&snd->refs, 1);
  SDL_SetAtomicInt(&v->state, V_PLAYING);
  if (out_idx)
    *out_idx = idx;
  if (out_gen)
    *out_gen = v->spawn_gen;
  return true;
}

// declared 台帳から見て voice がまだ生きているか
static bool voice_matches(AudioState *st, Declared *d) {
  Voice *v = &st->voices[d->voice_idx];
  int state = SDL_GetAtomicInt(&v->state);
  return (state == V_PLAYING || state == V_STOPPING) &&
         v->spawn_gen == d->voice_gen;
}

bool audio_play(AudioState *st, int id, float volume, float pitch, float pan) {
  audio_ensure_device(st);
  Snd *snd = snd_lookup(st, id);
  if (!snd)
    return false;
  return voice_spawn(st, snd, false, volume, pitch, pan, NULL, NULL);
}

bool audio_voice(AudioState *st, const char *key, int id, bool loop,
                 float volume, float pitch, float pan) {
  audio_ensure_device(st);
  if (!key || !key[0])
    return false;
  Snd *snd = snd_lookup(st, id);
  if (!snd)
    return false;

  Declared *entry = NULL;
  Declared *free_entry = NULL;
  for (int i = 0; i < AUDIO_MAX_VOICES; i++) {
    Declared *d = &st->declared[i];
    if (!d->in_use) {
      if (!free_entry)
        free_entry = d;
      continue;
    }
    if (strncmp(d->key, key, AUDIO_KEY_MAX) == 0) {
      entry = d;
      break;
    }
  }

  if (entry) {
    entry->declared_frame = st->frame;
    if (entry->ended)
      return true; // 自然終了済み。key が消えるまで再発火しない
    if (!voice_matches(st, entry)) {
      entry->ended = true; // サンプル末尾で終わった (非 loop の宣言 voice)
      return true;
    }
    Voice *v = &st->voices[entry->voice_idx];
    if (entry->snd_id == id && v->loop == loop) {
      SDL_SetAtomicU32(&v->t_vol, f2b(volume));
      SDL_SetAtomicU32(&v->t_pitch, f2b(pitch));
      SDL_SetAtomicU32(&v->t_pan, f2b(pan));
      return true;
    }
    // snd (または loop) が変わった: 旧 voice を fade out して張り替え
    SDL_CompareAndSwapAtomicInt(&v->state, V_PLAYING, V_STOPPING);
    entry->snd_id = id;
    return voice_spawn(st, snd, loop, volume, pitch, pan, &entry->voice_idx,
                       &entry->voice_gen);
  }

  if (!free_entry)
    return false;
  int vidx = 0;
  uint32_t vgen = 0;
  if (!voice_spawn(st, snd, loop, volume, pitch, pan, &vidx, &vgen))
    return false;
  memset(free_entry, 0, sizeof(*free_entry));
  strncpy(free_entry->key, key, AUDIO_KEY_MAX - 1);
  free_entry->in_use = true;
  free_entry->snd_id = id;
  free_entry->voice_idx = vidx;
  free_entry->voice_gen = vgen;
  free_entry->declared_frame = st->frame;
  return true;
}

void audio_master_volume(AudioState *st, float volume) {
  SDL_SetAtomicU32(&st->master, f2b(volume));
}

void audio_state_frame_end(AudioState *st) {
  // 宣言が途切れた declared voice を fade out へ
  for (int i = 0; i < AUDIO_MAX_VOICES; i++) {
    Declared *d = &st->declared[i];
    if (!d->in_use)
      continue;
    if (d->declared_frame == st->frame)
      continue;
    if (!d->ended && voice_matches(st, d)) {
      Voice *v = &st->voices[d->voice_idx];
      SDL_CompareAndSwapAtomicInt(&v->state, V_PLAYING, V_STOPPING);
    }
    d->in_use = false;
  }
  // freed snd の PCM 回収 (attach していた voice が全部離れてから)
  for (int i = 0; i < AUDIO_MAX_SNDS; i++) {
    Snd *snd = &st->snds[i];
    if (snd->in_use && !SDL_GetAtomicInt(&snd->alive) &&
        SDL_GetAtomicInt(&snd->refs) == 0) {
      free(snd->pcm);
      snd->pcm = NULL;
      snd->in_use = false;
    }
  }
  st->frame++;
}

void audio_state_info(AudioState *st, AudioInfo *out) {
  memset(out, 0, sizeof(*out));
  out->device_ok = st->device_ok;
  out->rate = st->device_ok ? st->device.sampleRate : 0;
  for (int i = 0; i < AUDIO_MAX_VOICES; i++)
    if (SDL_GetAtomicInt(&st->voices[i].state) != V_FREE)
      out->active_voices++;
  for (int i = 0; i < AUDIO_MAX_SNDS; i++)
    if (st->snds[i].in_use && SDL_GetAtomicInt(&st->snds[i].alive))
      out->snds++;
}

AudioState *audio_state_create(void) {
  AudioState *st = (AudioState *)calloc(1, sizeof(AudioState));
  if (!st)
    return NULL;
  SDL_SetAtomicU32(&st->master, f2b(1.0f));
  // device 起動前でも係数がゼロにならないよう既定値 (48kHz 相当)
  st->k_vol = 1.0f - expf(-1.0f / (0.005f * 48000.0f));
  st->k_pitch = 1.0f - expf(-1.0f / (0.020f * 48000.0f));
  return st;
}

void audio_state_destroy(AudioState *st) {
  if (!st)
    return;
  if (st->device_ok)
    ma_device_uninit(&st->device);
  if (st->context_inited)
    ma_context_uninit(&st->context);
  for (int i = 0; i < AUDIO_MAX_SNDS; i++)
    free(st->snds[i].pcm);
  free(st);
}

// ---------------------------------------------------------------------------
// decode utility (純関数、snd handle を作らない)

float *audio_decode_bytes(const void *data, size_t len, uint32_t *out_frames,
                          uint32_t *out_channels, uint32_t *out_rate) {
  if (!data || len == 0)
    return NULL;
  ma_decoder_config cfg = ma_decoder_config_init(ma_format_f32, 0, 0);
  ma_decoder dec;
  if (ma_decoder_init_memory(data, len, &cfg, &dec) != MA_SUCCESS)
    return NULL;
  uint32_t ch = dec.outputChannels;
  uint32_t rate = dec.outputSampleRate;
  if (ch == 0 || rate == 0) {
    ma_decoder_uninit(&dec);
    return NULL;
  }
  uint64_t cap = 16384;
  uint64_t total = 0;
  float *buf = (float *)malloc((size_t)cap * ch * sizeof(float));
  if (!buf) {
    ma_decoder_uninit(&dec);
    return NULL;
  }
  for (;;) {
    ma_uint64 read = 0;
    ma_result res =
        ma_decoder_read_pcm_frames(&dec, buf + total * ch, cap - total, &read);
    total += read;
    if (res != MA_SUCCESS || read == 0)
      break;
    if (total == cap) {
      cap *= 2;
      float *grown = (float *)realloc(buf, (size_t)cap * ch * sizeof(float));
      if (!grown) {
        free(buf);
        ma_decoder_uninit(&dec);
        return NULL;
      }
      buf = grown;
    }
  }
  ma_decoder_uninit(&dec);
  if (total < 2) {
    free(buf);
    return NULL;
  }
  *out_frames = (uint32_t)total;
  *out_channels = ch;
  *out_rate = rate;
  return buf;
}
