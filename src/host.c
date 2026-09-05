// Host bridge: a generic, game-agnostic message channel between game Lua and
// the page hosting the WASM build. lub itself has no networking; features
// like WebTransport/fetch live in the hosting page's JS and talk to the game
// through this bridge.
//
// JS contract (defined by the hosting page, e.g. --serve injects /host.js):
//
//   window.lubHost = {
//     queue: [],                        // inbound: {topic, payload} objects,
//                                       // payload is Uint8Array or string
//     onMessage(topic, payload) {...},  // outbound from game,
//                                       // payload arrives as Uint8Array
//   };
//
// C API (include/lub/lub_api.h): lub_host_available / lub_host_send /
// lub_host_poll。Lua binding は src/lua_api.c にあり、haxe-lib lub.Host が
// 同じ面を写す。
//
// Native builds have no hosting page: available() is false, send drops,
// poll returns false. (A native host could later be provided via
// package.loadlib without touching this file.)

#include "api_internal.h"
#include <stdlib.h>

#ifdef __EMSCRIPTEN__
#include <emscripten.h>

// EM_JS bodies are JavaScript; keep clang-format away from them (same guard
// and reasoning as the Slang bridge in shader.cpp).
// clang-format off
EM_JS(int, lub_host_available_js, (void), {
  return (typeof window !== 'undefined' && window.lubHost &&
          typeof window.lubHost.onMessage === 'function') ? 1 : 0;
});

EM_JS(void, lub_host_send_js,
      (const char *topic, const unsigned char *payload, int payload_len), {
  if (typeof window === 'undefined' || !window.lubHost ||
      typeof window.lubHost.onMessage !== 'function') {
    if (!window.__lubHostSendWarned) {
      window.__lubHostSendWarned = true;
      console.error('[lub] host_send dropped: window.lubHost.onMessage not defined');
    }
    return;
  }
  const topicStr = UTF8ToString(topic);
  const bytes = HEAPU8.slice(payload, payload + payload_len);
  try {
    window.lubHost.onMessage(topicStr, bytes);
  } catch (e) {
    console.error('[lub] lubHost.onMessage threw:', e);
  }
});

// Pops one message from window.lubHost.queue. Returns a malloc'd buffer
// laid out as [topic bytes][payload bytes] with the two lengths written to
// the out params, or 0 when the queue is empty. Caller frees.
EM_JS(unsigned char *, lub_host_poll_js, (int *topic_len, int *payload_len), {
  const h = (typeof window !== 'undefined') ? window.lubHost : null;
  if (!h || !h.queue || h.queue.length === 0)
    return 0;
  const msg = h.queue.shift();
  const enc = new TextEncoder();
  const topicBytes = enc.encode(String(msg.topic));
  const payload = (msg.payload instanceof Uint8Array)
                      ? msg.payload
                      : enc.encode(String(msg.payload ?? ''));
  const ptr = _malloc(topicBytes.length + payload.length);
  if (!ptr)
    return 0;
  HEAPU8.set(topicBytes, ptr);
  HEAPU8.set(payload, ptr + topicBytes.length);
  HEAP32[topic_len >> 2] = topicBytes.length;
  HEAP32[payload_len >> 2] = payload.length;
  return ptr;
});
// clang-format on
#endif

bool lub_host_available(LubContext *ctx) {
  (void)ctx;
#ifdef __EMSCRIPTEN__
  return lub_host_available_js() != 0;
#else
  return false;
#endif
}

void lub_host_send(LubContext *ctx, LubStr topic, LubStr payload) {
  (void)ctx;
#ifdef __EMSCRIPTEN__
  char tbuf[256];
  if (!lub_str_copy(topic, tbuf, sizeof(tbuf)))
    return;
  lub_host_send_js(tbuf, (const unsigned char *)payload.ptr, payload.len);
#else
  (void)topic;
  (void)payload;
#endif
}

LubStatus lub_host_poll(LubContext *ctx, LubStr *topic, LubStr *payload) {
  App *app = lub_api_app(ctx);
  // 直前の poll の buffer (view の実体) は次の poll で解放する。
  free(app->host_poll_buf);
  app->host_poll_buf = NULL;
  if (topic) {
    topic->ptr = NULL;
    topic->len = 0;
  }
  if (payload) {
    payload->ptr = NULL;
    payload->len = 0;
  }
#ifdef __EMSCRIPTEN__
  int topic_len = 0;
  int payload_len = 0;
  unsigned char *buf = lub_host_poll_js(&topic_len, &payload_len);
  if (buf) {
    app->host_poll_buf = buf;
    if (topic) {
      topic->ptr = (const char *)buf;
      topic->len = topic_len;
    }
    if (payload) {
      payload->ptr = (const char *)buf + topic_len;
      payload->len = payload_len;
    }
    return LUB_OK;
  }
#endif
  return LUB_NOT_FOUND;
}

void api_host_shutdown(App *app) {
  free(app->host_poll_buf);
  app->host_poll_buf = NULL;
}
