// lub audio: raw-PCM snd registry + voice pool, mixed by our own sampler.
// miniaudio is used only for device output (and pure decode utilities).
// The core resource contract is raw PCM only: only audio_snd_from_pcm creates a
// snd, and it knows nothing about file formats.
//
// Voice has two lifetime policies:
//   - oneshot (audio_play): released automatically at the end of the sample.
//     Parameters are fixed when fired.
//   - declared (audio_voice): declared every frame with a key.
//     When the declarations stop, it fades out.
//     The same key continues, keeping its playback position, and
//     volume/pitch/pan are smoothed on the audio side toward the declared
//     values. pitch also allows 0 (stopped) and negative values (reverse
//     playback).
//
// Threading: the game thread writes the snd registry and voice target values,
// and the audio callback advances the playback position and current values.
// Only the SDL atomic state / target cross the boundary, and no locks are used.
#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

typedef struct AudioState AudioState;

typedef struct AudioInfo {
  bool device_ok; // Whether the output device (including the null backend) is
                  // running
  uint32_t rate;  // Output sample rate of the device (0 if there is no device)
  int active_voices; // Number of voice slots that are not FREE
  int snds;          // Number of live snds
} AudioInfo;

// All APIs are for the game thread only.
// AudioState lazily initializes its internals on the first call (including
// starting the device). App owns create/destroy.
AudioState *audio_state_create(void);
void audio_state_destroy(AudioState *st);

// Call at the end of the frame: moves declared voices that were not declared
// this frame to fade out and reclaims the PCM of freed snds.
void audio_state_frame_end(AudioState *st);

// Create a snd from interleaved f32 PCM.
// It dedupes by content (bytes + channels + rate), so recreating the same
// waveform after a hot reload returns the same id, and declared voices that are
// playing are not interrupted. Returns a handle of 1 or more, or 0 on failure
// (full / invalid arguments).
int audio_snd_from_pcm(AudioState *st, const float *interleaved,
                       uint32_t frames, uint32_t channels, uint32_t rate);

// Retire a snd.
// Later lookups (play / voice) fail, but playing voices play to the end, and
// the PCM is reclaimed in frame_end once no references remain. Declaring again
// with the same contents brings back the same snd through dedupe.
bool audio_snd_retire(AudioState *st, int snd);

// oneshot.
// If pitch<0, playback starts from the end in reverse.
bool audio_play(AudioState *st, int snd, float volume, float pitch, float pan);

// Maximum key length for voices and snds (including the terminating NUL).
// Matches the length of gfx keys.
#define AUDIO_KEY_MAX 256

// declared voice.
// Call every frame.
// As long as the key is the same, the same voice is updated.
// If snd changes, fade out the old voice and start a new one.
bool audio_voice(AudioState *st, const char *key, int snd, bool loop,
                 float volume, float pitch, float pan);

void audio_master_volume(AudioState *st, float volume);

void audio_state_info(AudioState *st, AudioInfo *out);

// file format bytes → malloc'ed interleaved f32 PCM.
// A pure-function utility (on par with png_load) that does not create a snd
// handle. NULL on failure.
float *audio_decode_bytes(const void *data, size_t len, uint32_t *out_frames,
                          uint32_t *out_channels, uint32_t *out_rate);
