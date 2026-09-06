// lub audio: raw-PCM snd registry + voice pool, mixed by our own sampler.
// miniaudio は device 出力 (と pure な decode utility) としてだけ使う。
// core の resource 契約は raw PCM のみ: snd を生むのは audio_snd_from_pcm
// だけで、file format は知らない (docs/roadmap.md Planned Areas)。
//
// Voice は2種類の寿命ポリシーを持つ:
//   - oneshot (audio_play): サンプル末尾で自動解放。パラメータは発火時固定。
//   - declared (audio_voice): key 付きで毎フレーム宣言。宣言が途切れたら
//     fade out。同一 key は再生位置を保って継続し、volume/pitch/pan は
//     宣言値を目標に audio 側で平滑化される。pitch は 0 (停止) と負値
//     (逆再生) も許す。
//
// Threading: game thread は snd registry と voice の target 値を書き、
// audio callback は再生位置と current 値を進める。境界を渡るのは
// SDL atomic の state / target だけで、lock は使わない。
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct AudioState AudioState;

typedef struct AudioInfo {
  bool device_ok;    // 出力デバイス (null backend 含む) が動いているか
  uint32_t rate;     // device の出力サンプルレート (device 無しなら 0)
  int active_voices; // FREE でない voice slot 数
  int snds;          // 生存している snd 数
} AudioInfo;

// 全 API は game thread 専用。AudioState は初回の呼び出しで内部を lazy に
// 初期化する (デバイス起動含む)。create/destroy は App が所有する。
AudioState *audio_state_create(void);
void audio_state_destroy(AudioState *st);

// フレーム末に呼ぶ: 今フレーム宣言されなかった declared voice を fade out
// に落とし、free 済み snd の PCM を回収する。
void audio_state_frame_end(AudioState *st);

// interleaved f32 PCM から snd を作る。内容 (bytes + channels + rate) で
// dedupe するので、hot reload 後に同じ波形を作り直しても同じ id が返り、
// 鳴っている declared voice は途切れない。返り値は 1 以上の handle、
// 失敗 (満杯 / 引数不正) は 0。
int audio_snd_from_pcm(AudioState *st, const float *interleaved,
                       uint32_t frames, uint32_t channels, uint32_t rate);

// snd を退役させる。以後の lookup (play / voice) は失敗するが、鳴っている
// voice は最後まで鳴り、参照が無くなってから frame_end で PCM を回収する。
// 同じ内容で宣言し直せば dedupe で同じ snd が復帰する。
bool audio_snd_retire(AudioState *st, int snd);

// oneshot。pitch<0 なら末尾から逆再生で始まる。
bool audio_play(AudioState *st, int snd, float volume, float pitch, float pan);

// declared voice。毎フレーム呼ぶこと。key が同じ間は同じ voice を更新する。
// snd が変わったら旧 voice を fade out して新しく始める。
bool audio_voice(AudioState *st, const char *key, int snd, bool loop,
                 float volume, float pitch, float pan);

void audio_master_volume(AudioState *st, float volume);

void audio_state_info(AudioState *st, AudioInfo *out);

// file format bytes → malloc した interleaved f32 PCM。純関数 utility
// (png_load と同格) で、snd handle は作らない。失敗は NULL。
float *audio_decode_bytes(const void *data, size_t len, uint32_t *out_frames,
                          uint32_t *out_channels, uint32_t *out_rate);
