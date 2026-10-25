// lubx.vorbis (cs-lib/lub_stub.cs の namespace Lubx): Ogg Vorbis の bytes を f32 PCM に戻す。
// core API ではなく、lubx (cs-lib/lubx/Assets.cs の Ogg) が使う C の実装。
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
    return LUB_NOT_FOUND; // Ogg Vorbis でない / 壊れた data は nil で返す
  stb_vorbis_info info = stb_vorbis_get_info(v);
  int ch = info.channels;
  // 長さは末尾の page の granule から分かる。分からない (壊れた) stream は読めないものとして扱う。
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
  app_frame_garbage_push(app, out); // view の実体は frame の終わりに回収
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
