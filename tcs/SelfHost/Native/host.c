/* セルフホストの compiler の C の build の host (tcs2c の --lib)。
   tcs2c が出した tcsc.c を include し、HostStub.cs の宣言を実装する。
   compile: gcc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard -I <tcsc.c の dir> host.c -o tcsc -lm */
#include "tcsc.c"

#include <stdio.h>
#include <string.h>

static int host_argc;
static char **host_argv;
static int host_exit_code;
static int host_done;

static TcsString *host_string(const unsigned char *data, size_t length)
{
    return tcs_string_new(data, length);
}

int32_t tcs_host_tcschost_arg_count(void)
{
    return (int32_t)(host_argc - 1);
}

TcsString *tcs_host_tcschost_arg(int32_t index)
{
    const char *s = host_argv[index + 1];
    return host_string((const unsigned char *)s, strlen(s));
}

static char *host_cstring(TcsString *s)
{
    char *p = malloc(s->length + 1);
    if (p == NULL) tcs_fault("out-of-memory");
    memcpy(p, s->data, s->length);
    p[s->length] = 0;
    return p;
}

TcsString *tcs_host_tcschost_read_file(TcsString *path)
{
    char *name = host_cstring(path);
    FILE *f = fopen(name, "rb");
    if (f == NULL)
    {
        fprintf(stderr, "error: cannot read %s\n", name);
        exit(2);
    }
    fseek(f, 0, SEEK_END);
    long size = ftell(f);
    fseek(f, 0, SEEK_SET);
    unsigned char *buf = malloc(size > 0 ? (size_t)size : 1);
    if (buf == NULL) tcs_fault("out-of-memory");
    size_t got = fread(buf, 1, (size_t)size, f);
    fclose(f);
    free(name);
    TcsString *text = host_string(buf, got);
    free(buf);
    return text;
}

void tcs_host_tcschost_write_file(TcsString *path, TcsString *text)
{
    char *name = host_cstring(path);
    FILE *f = fopen(name, "wb");
    if (f == NULL)
    {
        fprintf(stderr, "error: cannot write %s\n", name);
        exit(2);
    }
    fwrite(text->data, 1, text->length, f);
    fclose(f);
    free(name);
}

void tcs_host_tcschost_write_out(TcsString *text)
{
    fwrite(text->data, 1, text->length, stdout);
}

void tcs_host_tcschost_write_err(TcsString *text)
{
    fwrite(text->data, 1, text->length, stderr);
}

void tcs_host_tcschost_set_exit_code(int32_t code)
{
    host_exit_code = code;
}

void tcs_host_tcschost_set_done(void)
{
    host_done = 1;
}

/* TCSC_HEAP=1 なら、段ごとの managed heap の大きさの最大を stderr に出す (entry の間が GC の frame の境界) */
int main(int argc, char **argv)
{
    host_argc = argc;
    host_argv = argv;
    tcs_lib_init();
    const char *heap = getenv("TCSC_HEAP");
    size_t peak = 0;
    int steps = 0;
    tcs_entry_TcscMain_Start();
    while (!host_done)
    {
        tcs_entry_TcscMain_Step();
        steps++;
        size_t now = tcs_lib_heap_bytes();
        if (now > peak) peak = now;
    }
    if (heap != NULL && heap[0] == '1')
        fprintf(stderr, "steps %d, peak managed heap between steps %zu bytes\n", steps, peak);
    fflush(stdout);
    return host_exit_code;
}
