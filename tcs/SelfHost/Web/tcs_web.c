/* WebAssembly host of tcs (Emscripten).
   Includes tcs.c, which tcs2c generated from SelfHost/Compiler and
   SelfHost/Tool, and implements the declarations in SelfHost/Tool/TcsHost.cs.
   JS (tcs.mjs) puts the request in the arguments and calls tcs_web_call, and
   receives the reply that Playground.Call wrote with TcsHost.WriteOut. There
   are no files; JS passes the files shipped with tcs (lib/TinySystem/*.cs) in
   advance with tcs_web_tool_file. */
#include "tcs.c"

#include <emscripten.h>
#include <string.h>

typedef struct HostBytes {
  char *data;
  size_t length;
} HostBytes;

static HostBytes *host_args;
static int host_arg_count;
static int host_arg_capacity;
static char *host_out;
static size_t host_out_length;
static size_t host_out_capacity;
static HostBytes *host_tool_names;
static HostBytes *host_tool_texts;
static int host_tool_count;
static int host_tool_capacity;

static HostBytes host_copy(const char *data, int length) {
  HostBytes b;
  b.data = malloc(length > 0 ? (size_t)length : 1);
  if (b.data == NULL)
    tcs_fault("out-of-memory");
  memcpy(b.data, data, (size_t)length);
  b.length = (size_t)length;
  return b;
}

static TcsString *host_string(const char *data, size_t length) {
  return tcs_string_new((const unsigned char *)data, length);
}

EMSCRIPTEN_KEEPALIVE void tcs_web_init(void) { tcs_lib_init(); }

EMSCRIPTEN_KEEPALIVE void tcs_web_clear_args(void) {
  for (int i = 0; i < host_arg_count; i++)
    free(host_args[i].data);
  host_arg_count = 0;
}

EMSCRIPTEN_KEEPALIVE void tcs_web_push_arg(const char *data, int length) {
  if (host_arg_count == host_arg_capacity) {
    host_arg_capacity = host_arg_capacity ? host_arg_capacity * 2 : 16;
    host_args =
        realloc(host_args, sizeof(HostBytes) * (size_t)host_arg_capacity);
    if (host_args == NULL)
      tcs_fault("out-of-memory");
  }
  host_args[host_arg_count++] = host_copy(data, length);
}

EMSCRIPTEN_KEEPALIVE void tcs_web_tool_file(const char *name, int name_length,
                                            const char *text, int text_length) {
  if (host_tool_count == host_tool_capacity) {
    host_tool_capacity = host_tool_capacity ? host_tool_capacity * 2 : 8;
    host_tool_names = realloc(host_tool_names,
                              sizeof(HostBytes) * (size_t)host_tool_capacity);
    host_tool_texts = realloc(host_tool_texts,
                              sizeof(HostBytes) * (size_t)host_tool_capacity);
    if (host_tool_names == NULL || host_tool_texts == NULL)
      tcs_fault("out-of-memory");
  }
  host_tool_names[host_tool_count] = host_copy(name, name_length);
  host_tool_texts[host_tool_count] = host_copy(text, text_length);
  host_tool_count++;
}

/* Handles one request and returns the start of the reply (the length is
 * tcs_web_out_length). Returning from the entry is a frame boundary for the
 * tcs2c GC */
EMSCRIPTEN_KEEPALIVE const char *tcs_web_call(void) {
  host_out_length = 0;
  tcs_entry_TcsSelf_Playground_Call();
  return host_out != NULL ? host_out : "";
}

EMSCRIPTEN_KEEPALIVE int tcs_web_out_length(void) {
  return (int)host_out_length;
}

int32_t tcs_host_tcshost_arg_count(void) { return host_arg_count; }

TcsString *tcs_host_tcshost_arg(int32_t index) {
  return host_string(host_args[index].data, host_args[index].length);
}

bool tcs_host_tcshost_file_exists(TcsString *path) {
  (void)path;
  return false;
}

bool tcs_host_tcshost_same_file(TcsString *a, TcsString *b) {
  (void)a;
  (void)b;
  return false;
}

TcsString *tcs_host_tcshost_read_file(TcsString *path) {
  (void)path;
  return host_string("", 0);
}

TcsString *tcs_host_tcshost_write_file_atomic(TcsString *path,
                                              TcsString *text) {
  (void)path;
  (void)text;
  const char *message = "the web host has no files";
  return host_string(message, strlen(message));
}

TcsString *tcs_host_tcshost_write_binary_atomic(TcsString *path,
                                                TcsString *bytes) {
  return tcs_host_tcshost_write_file_atomic(path, bytes);
}

void tcs_host_tcshost_write_out(TcsString *text) {
  if (host_out_length + text->length + 1 > host_out_capacity) {
    size_t cap = host_out_capacity ? host_out_capacity : 65536;
    while (cap < host_out_length + text->length + 1)
      cap *= 2;
    host_out = realloc(host_out, cap);
    if (host_out == NULL)
      tcs_fault("out-of-memory");
    host_out_capacity = cap;
  }
  memcpy(host_out + host_out_length, text->data, text->length);
  host_out_length += text->length;
  host_out[host_out_length] = 0;
}

void tcs_host_tcshost_write_err(TcsString *text) { (void)text; }

void tcs_host_tcshost_flush_out(void) {}

TcsString *tcs_host_tcshost_file_stamp(TcsString *path) {
  (void)path;
  return host_string("", 0);
}

TcsString *tcs_host_tcshost_tool_file(TcsString *name) {
  for (int i = 0; i < host_tool_count; i++) {
    if (host_tool_names[i].length == name->length &&
        memcmp(host_tool_names[i].data, name->data, name->length) == 0)
      return host_string(host_tool_texts[i].data, host_tool_texts[i].length);
  }
  return host_string("", 0);
}

void tcs_host_tcshost_sleep(int32_t milliseconds) { (void)milliseconds; }

void tcs_host_tcshost_set_exit_code(int32_t code) { (void)code; }

void tcs_host_tcshost_set_done(void) {}
