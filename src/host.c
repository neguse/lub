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
// Lua API (globals, mirrored by haxe-lib lub.Host):
//
//   host_available() -> bool
//   host_send(topic, payload)          -- payload is a binary-safe string
//   host_poll() -> topic, payload      -- one message; nil when queue empty
//
// Native builds have no hosting page: available() is false, send drops,
// poll returns nil. (A native host could later be provided via
// package.loadlib without touching this file.)

#include "host.h"
#include <lauxlib.h>
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

static int l_host_available(lua_State *L) {
#ifdef __EMSCRIPTEN__
  lua_pushboolean(L, lub_host_available_js());
#else
  lua_pushboolean(L, 0);
#endif
  return 1;
}

static int l_host_send(lua_State *L) {
  size_t payload_len = 0;
  const char *topic = luaL_checkstring(L, 1);
  const char *payload = luaL_checklstring(L, 2, &payload_len);
#ifdef __EMSCRIPTEN__
  lub_host_send_js(topic, (const unsigned char *)payload, (int)payload_len);
#else
  (void)topic;
  (void)payload;
#endif
  return 0;
}

static int l_host_poll(lua_State *L) {
#ifdef __EMSCRIPTEN__
  int topic_len = 0;
  int payload_len = 0;
  unsigned char *buf = lub_host_poll_js(&topic_len, &payload_len);
  if (buf) {
    lua_pushlstring(L, (const char *)buf, (size_t)topic_len);
    lua_pushlstring(L, (const char *)buf + topic_len, (size_t)payload_len);
    free(buf);
    return 2;
  }
#endif
  lua_pushnil(L);
  return 1;
}

void host_lua_register(lua_State *L) {
  lua_pushcfunction(L, l_host_available);
  lua_setglobal(L, "host_available");
  lua_pushcfunction(L, l_host_send);
  lua_setglobal(L, "host_send");
  lua_pushcfunction(L, l_host_poll);
  lua_setglobal(L, "host_poll");
}
