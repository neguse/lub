// lubx.vorbis (namespace Lubx in cs-lib/lub_stub.cs): decodes Ogg Vorbis bytes
// into f32 PCM. Not a core API; a C implementation used by lubx (Ogg in
// cs-lib/lubx/Assets.cs).
#include "api_internal.h"
#include <stdlib.h>

#define STB_VORBIS_NO_STDIO
#define STB_VORBIS_NO_PUSHDATA_API
#include "stb_vorbis.c"

LubStatus lubx_vorbis_decode(LubContext *ctx, const uint8_t *data,
                             int32_t data_len, LubView *pcm, int32_t *channels,
                             int32_t *rate) {
  App *app = lub_api_app(ctx);
  if (!data || data_len <= 0)
    return lub_api_fail(app, "vorbis.decode: empty data");
  int error = 0;
  stb_vorbis *v = stb_vorbis_open_memory(data, data_len, &error, NULL);
  if (!v)
    return LUB_NOT_FOUND; // Data that is not Ogg Vorbis / is corrupt returns
                          // nil
  stb_vorbis_info info = stb_vorbis_get_info(v);
  int ch = info.channels;
  // The length is known from the granule of the last page. A stream where it is
  // unknown (corrupt) is treated as unreadable.
  size_t frames = stb_vorbis_stream_length_in_samples(v);
  float *out =
      frames ? (float *)malloc(frames * (size_t)ch * sizeof(float)) : NULL;
  if (out)
    frames = (size_t)stb_vorbis_get_samples_float_interleaved(
        v, ch, out, (int)(frames * (size_t)ch));
  stb_vorbis_close(v);
  if (!frames) {
    free(out);
    return LUB_NOT_FOUND;
  }
  if (!out)
    return lub_api_fail(app, "vorbis.decode: out of memory");
  app_frame_garbage_push(
      app, out); // The view's backing data is reclaimed at the end of the frame
  if (pcm) {
    pcm->ptr = (const uint8_t *)out;
    pcm->len = (int32_t)(frames * (size_t)ch * sizeof(float));
    pcm->frame = (int32_t)app->frame_index;
  }
  if (channels)
    *channels = ch;
  if (rate)
    *rate = (int32_t)info.sample_rate;
  return LUB_OK;
}
