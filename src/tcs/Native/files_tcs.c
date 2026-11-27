/* The functions of Api/TcsFiles.cs for a C module made by `tcs c`: turns the
   module's strings (TcsString) into C strings and calls Native/files.c.
   Include it after the generated C of the module.
   Shipped is not here: where the shipped files are depends on the program (the
   dir of the executable, or files embedded in it) */
#include "files.c"

static char *tcs_files_cstring(TcsString *s) {
  char *p = tcs_files_alloc(s->length + 1);
  memcpy(p, s->data, s->length);
  p[s->length] = 0;
  return p;
}

static TcsString *tcs_files_string(const char *data, size_t length) {
  return tcs_string_new((const unsigned char *)data, length);
}

/* The text read from a C path, as a module string. NULL (the module's null) if
 * it cannot be read */
static TcsString *tcs_files_read_string(const char *path) {
  size_t length;
  char *buf = tcs_files_read(path, &length);
  if (buf == NULL)
    return NULL;
  TcsString *text = tcs_files_string(buf, length);
  free(buf);
  return text;
}

TcsString *tcs_host_tcsfiles_read(TcsString *path) {
  char *name = tcs_files_cstring(path);
  TcsString *text = tcs_files_read_string(name);
  free(name);
  return text;
}

bool tcs_host_tcsfiles_same_file(TcsString *a, TcsString *b) {
  char *na = tcs_files_cstring(a);
  char *nb = tcs_files_cstring(b);
  int same = tcs_files_same(na, nb);
  free(na);
  free(nb);
  return same != 0;
}

TcsString *tcs_host_tcsfiles_stamp(TcsString *path) {
  char *name = tcs_files_cstring(path);
  char stamp[96];
  tcs_files_stamp(name, stamp);
  free(name);
  return tcs_files_string(stamp, strlen(stamp));
}

TcsString *tcs_host_tcsfiles_write_text(TcsString *path, TcsString *text) {
  char *name = tcs_files_cstring(path);
  char *failure = tcs_files_write(name, (const char *)text->data, text->length);
  free(name);
  if (failure == NULL)
    return tcs_files_string("", 0);
  TcsString *reason = tcs_files_string(failure, strlen(failure));
  free(failure);
  return reason;
}

/* A module string is a byte sequence, so writing bytes is the same as writing
 * text */
TcsString *tcs_host_tcsfiles_write_bytes(TcsString *path, TcsString *bytes) {
  return tcs_host_tcsfiles_write_text(path, bytes);
}
