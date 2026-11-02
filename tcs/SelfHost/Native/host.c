/* tcs のネイティブの build の host。
   tcs2c が SelfHost/Compiler と SelfHost/Tool を C にした tcs.c を include し、SelfHost/Tool/TcsHost.cs の宣言 (tcs2c の --ref) を実装する。
   CLI の本体は SelfHost/Tool/Cli.cs で、host は Cli.Start を 1 回、TcsHost.SetDone まで Cli.Step を呼ぶ。
   entry から戻るたびに tcs2c の GC の frame の境界になる。
   tcs と一緒に配るファイル (runtime/*.lua、lib/TinySystem/*.cs) は環境変数 TCS_HOME の dir か、実行ファイルの dir から読む。
   compile: cc -O2 -ffp-contract=off -fwrapv -fexcess-precision=standard -I <tcs.c の dir> host.c -o tcs -lm */
#include "tcs.c"

#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include <sys/stat.h>

#ifdef _WIN32
#include <direct.h>
#include <fcntl.h>
#include <io.h>
#include <process.h>
#include <windows.h>
#define getpid _getpid
#else
#include <limits.h>
#include <unistd.h>
#endif
#ifdef __APPLE__
#include <mach-o/dyld.h>
#endif

static int host_argc;
static char **host_argv;
static int host_exit_code;
static int host_done;
static char *host_home;

static TcsString *host_string(const char *data, size_t length)
{
    return tcs_string_new((const unsigned char *)data, length);
}

static TcsString *host_cstr(const char *s)
{
    return host_string(s, strlen(s));
}

static char *host_cstring(TcsString *s)
{
    char *p = malloc(s->length + 1);
    if (p == NULL) tcs_fault("out-of-memory");
    memcpy(p, s->data, s->length);
    p[s->length] = 0;
    return p;
}

/* ファイルの内容。読めなければ NULL */
static char *host_read_all(FILE *f, size_t *length)
{
    size_t cap = 65536, len = 0;
    char *buf = malloc(cap);
    if (buf == NULL) tcs_fault("out-of-memory");
    for (;;)
    {
        size_t got = fread(buf + len, 1, cap - len, f);
        len += got;
        if (len < cap) break;
        cap *= 2;
        buf = realloc(buf, cap);
        if (buf == NULL) tcs_fault("out-of-memory");
    }
    *length = len;
    return buf;
}

static TcsString *host_read_path(const char *name)
{
    FILE *f = fopen(name, "rb");
    if (f == NULL) return host_string("", 0);
    size_t length;
    char *buf = host_read_all(f, &length);
    fclose(f);
    /* UTF-8 の BOM は .NET の File.ReadAllText と同じく除く */
    size_t skip = length >= 3 && (unsigned char)buf[0] == 0xEF && (unsigned char)buf[1] == 0xBB && (unsigned char)buf[2] == 0xBF ? 3 : 0;
    TcsString *text = host_string(buf + skip, length - skip);
    free(buf);
    return text;
}

int32_t tcs_host_tcshost_arg_count(void)
{
    return (int32_t)(host_argc - 1);
}

TcsString *tcs_host_tcshost_arg(int32_t index)
{
    return host_cstr(host_argv[index + 1]);
}

static int host_is_file(const char *name)
{
    struct stat st;
    return stat(name, &st) == 0 && (st.st_mode & S_IFMT) == S_IFREG;
}

bool tcs_host_tcshost_file_exists(TcsString *path)
{
    char *name = host_cstring(path);
    int ok = host_is_file(name);
    free(name);
    return ok != 0;
}

TcsString *tcs_host_tcshost_read_file(TcsString *path)
{
    char *name = host_cstring(path);
    TcsString *text = host_read_path(name);
    free(name);
    return text;
}

TcsString *tcs_host_tcshost_read_stdin(void)
{
    size_t length;
    char *buf = host_read_all(stdin, &length);
    TcsString *text = host_string(buf, length);
    free(buf);
    return text;
}

static int host_is_sep(char c)
{
#ifdef _WIN32
    return c == '/' || c == '\\';
#else
    return c == '/';
#endif
}

/* 絶対 path にして `.` と `..` と重なった区切りを除く (.NET の Path.GetFullPath と同じ形)。戻り値は malloc */
#ifdef _WIN32
/* Windows は OS に任せる (drive の無い `\x`、drive 相対の `C:x`、UNC の `\\server\share` も .NET と同じに解く) */
static char *host_full_path(const char *path)
{
    DWORD need = GetFullPathNameA(path, 0, NULL, NULL);
    char *out = malloc((need > 0 ? need : strlen(path)) + 2);
    if (out == NULL) tcs_fault("out-of-memory");
    if (need == 0 || GetFullPathNameA(path, need + 1, out, NULL) == 0) strcpy(out, path);
    return out;
}
#else
static char *host_full_path(const char *path)
{
    char cwd[4096];
    size_t plen = strlen(path);
    int absolute = plen > 0 && path[0] == '/';
    char *joined;
    if (absolute)
    {
        joined = malloc(plen + 1);
        if (joined == NULL) tcs_fault("out-of-memory");
        memcpy(joined, path, plen + 1);
    }
    else
    {
        if (getcwd(cwd, sizeof cwd) == NULL) cwd[0] = 0;
        size_t clen = strlen(cwd);
        joined = malloc(clen + plen + 2);
        if (joined == NULL) tcs_fault("out-of-memory");
        memcpy(joined, cwd, clen);
        joined[clen] = '/';
        memcpy(joined + clen + 1, path, plen + 1);
    }
    size_t n = strlen(joined);
    char *out = malloc(n + 2);
    if (out == NULL) tcs_fault("out-of-memory");
    size_t o = 0;
    size_t i = 0;
    while (i < n)
    {
        while (i < n && host_is_sep(joined[i])) i++;
        size_t start = i;
        while (i < n && !host_is_sep(joined[i])) i++;
        size_t len = i - start;
        if (len == 0 || (len == 1 && joined[start] == '.')) continue;
        if (len == 2 && joined[start] == '.' && joined[start + 1] == '.')
        {
            while (o > 0 && !host_is_sep(out[o - 1])) o--;
            if (o > 0) o--;
            continue;
        }
        out[o++] = '/';
        memcpy(out + o, joined + start, len);
        o += len;
    }
    if (o == 0) out[o++] = '/';
    out[o] = 0;
    /* 末尾の区切りは .NET と同じく残す */
    if (plen > 0 && host_is_sep(path[plen - 1]) && !host_is_sep(out[o - 1]))
    {
        out[o++] = path[plen - 1];
        out[o] = 0;
    }
    free(joined);
    return out;
}
#endif

static int host_mkdirs(const char *dir)
{
    struct stat st;
    if (stat(dir, &st) == 0) return 0;
    size_t n = strlen(dir);
    char *buf = malloc(n + 1);
    if (buf == NULL) tcs_fault("out-of-memory");
    memcpy(buf, dir, n + 1);
    size_t cut = n;
    while (cut > 0 && !host_is_sep(buf[cut - 1])) cut--;
    if (cut > 1)
    {
        buf[cut - 1] = 0;
        host_mkdirs(buf);
    }
    free(buf);
#ifdef _WIN32
    return _mkdir(dir);
#else
    return mkdir(dir, 0777);
#endif
}

static TcsString *host_error_text(const char *what, const char *path)
{
    char buf[4400];
    snprintf(buf, sizeof buf, "%s '%s': %s", what, path, strerror(errno));
    return host_cstr(buf);
}

/* 一時ファイルに書いてから置き換える (書きかけの出力を読ませない)。失敗したら理由を返す */
TcsString *tcs_host_tcshost_write_file_atomic(TcsString *path, TcsString *text)
{
    char *name = host_cstring(path);
    char *full = host_full_path(name);
    free(name);
    size_t n = strlen(full);
    size_t cut = n;
    while (cut > 0 && !host_is_sep(full[cut - 1])) cut--;
    char *dir = malloc(cut + 1);
    if (dir == NULL) tcs_fault("out-of-memory");
    memcpy(dir, full, cut);
    dir[cut > 1 ? cut - 1 : cut] = 0;
    host_mkdirs(dir);
    TcsString *failure = host_string("", 0);
    struct stat st;
    int exists = stat(full, &st) == 0;
    if (exists)
    {
        FILE *probe = fopen(full, "r+b");
        if (probe == NULL)
        {
            failure = host_error_text("cannot write", full);
            free(dir);
            free(full);
            return failure;
        }
        fclose(probe);
    }
    char *tmp = malloc(n + 64);
    if (tmp == NULL) tcs_fault("out-of-memory");
    snprintf(tmp, n + 64, "%s/.%s.%d.tmp", dir, full + cut, (int)getpid());
    FILE *f = fopen(tmp, "wb");
    if (f == NULL)
        failure = host_error_text("cannot write", tmp);
    else
    {
        int ok = fwrite(text->data, 1, text->length, f) == text->length;
        ok = fclose(f) == 0 && ok;
#ifndef _WIN32
        if (ok && exists) chmod(tmp, st.st_mode & 07777);
#else
        if (ok && exists) remove(full);
#endif
        if (!ok || rename(tmp, full) != 0)
        {
            failure = host_error_text("cannot write", full);
            remove(tmp);
        }
    }
    free(tmp);
    free(dir);
    free(full);
    return failure;
}

/* C の文字列は byte 列そのものなので、byte 列の書き込みは文字列の書き込みと同じ */
TcsString *tcs_host_tcshost_write_binary_atomic(TcsString *path, TcsString *bytes)
{
    return tcs_host_tcshost_write_file_atomic(path, bytes);
}

void tcs_host_tcshost_write_out(TcsString *text)
{
    fwrite(text->data, 1, text->length, stdout);
}

void tcs_host_tcshost_write_err(TcsString *text)
{
    fwrite(text->data, 1, text->length, stderr);
}

void tcs_host_tcshost_flush_out(void)
{
    fflush(stdout);
}

TcsString *tcs_host_tcshost_env(TcsString *name)
{
    char *key = host_cstring(name);
    const char *value = getenv(key);
    free(key);
    return host_cstr(value != NULL ? value : "");
}

TcsString *tcs_host_tcshost_path_key(TcsString *path)
{
    char *name = host_cstring(path);
    char *full = host_full_path(name);
    free(name);
#ifdef _WIN32
    for (char *p = full; *p; p++)
        if (*p >= 'A' && *p <= 'Z') *p = (char)(*p - 'A' + 'a');
#endif
    TcsString *key = host_cstr(full);
    free(full);
    return key;
}

TcsString *tcs_host_tcshost_file_identity(TcsString *path)
{
#ifdef _WIN32
    /* volume の番号とファイルの番号 (hard link や junction を通しても同じ値)。.NET の FileIdentityReader と同じ情報 */
    char *wname = host_cstring(path);
    HANDLE h = CreateFileA(wname, 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING,
                           FILE_FLAG_BACKUP_SEMANTICS, NULL);
    free(wname);
    if (h == INVALID_HANDLE_VALUE) return host_string("", 0);
    BY_HANDLE_FILE_INFORMATION info;
    int got = GetFileInformationByHandle(h, &info) != 0;
    CloseHandle(h);
    if (!got) return host_cstr("-");
    char wbuf[64];
    snprintf(wbuf, sizeof wbuf, "%lu:%lu:%lu", (unsigned long)info.dwVolumeSerialNumber,
             (unsigned long)info.nFileIndexLow, (unsigned long)info.nFileIndexHigh);
    return host_cstr(wbuf);
#else
    char *name = host_cstring(path);
    struct stat st;
    int ok = stat(name, &st) == 0;
    free(name);
    if (!ok) return host_string("", 0);
    char buf[64];
    snprintf(buf, sizeof buf, "%llu:%llu:0", (unsigned long long)st.st_dev, (unsigned long long)st.st_ino);
    return host_cstr(buf);
#endif
}

TcsString *tcs_host_tcshost_file_stamp(TcsString *path)
{
#ifdef _WIN32
    /* 最終書き込み時刻は 100ns 単位 (stat の st_mtime は秒で、同じ秒の同じ大きさの保存を見落とす) */
    char *wname = host_cstring(path);
    WIN32_FILE_ATTRIBUTE_DATA data;
    int found = GetFileAttributesExA(wname, GetFileExInfoStandard, &data) != 0;
    free(wname);
    if (!found || (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0) return host_string("", 0);
    long long ticks = ((long long)data.ftLastWriteTime.dwHighDateTime << 32) | data.ftLastWriteTime.dwLowDateTime;
    long long size = ((long long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
    char wbuf[96];
    snprintf(wbuf, sizeof wbuf, "%lld:%lld", ticks, size);
    return host_cstr(wbuf);
#else
    char *name = host_cstring(path);
    struct stat st;
    int ok = stat(name, &st) == 0 && (st.st_mode & S_IFMT) == S_IFREG;
    free(name);
    if (!ok) return host_string("", 0);
    char buf[96];
#if defined(__APPLE__)
    long long ns = (long long)st.st_mtimespec.tv_sec * 1000000000LL + st.st_mtimespec.tv_nsec;
#else
    long long ns = (long long)st.st_mtim.tv_sec * 1000000000LL + st.st_mtim.tv_nsec;
#endif
    snprintf(buf, sizeof buf, "%lld:%lld", ns, (long long)st.st_size);
    return host_cstr(buf);
#endif
}

/* tcs と一緒に配るファイルの dir (TCS_HOME か、実行ファイルの dir) */
static const char *host_tool_home(void)
{
    if (host_home != NULL) return host_home;
    const char *env = getenv("TCS_HOME");
    char exe[4096];
    exe[0] = 0;
    if (env != NULL && env[0] != 0)
    {
        snprintf(exe, sizeof exe, "%s/", env);
    }
    else
    {
#if defined(_WIN32)
        DWORD got = GetModuleFileNameA(NULL, exe, sizeof exe);
        if (got == 0 || got >= sizeof exe) exe[0] = 0;
#elif defined(__APPLE__)
        uint32_t size = sizeof exe;
        if (_NSGetExecutablePath(exe, &size) != 0) exe[0] = 0;
#else
        ssize_t got = readlink("/proc/self/exe", exe, sizeof exe - 1);
        exe[got > 0 ? got : 0] = 0;
#endif
        if (exe[0] == 0 && host_argv != NULL && host_argv[0] != NULL)
            snprintf(exe, sizeof exe, "%s", host_argv[0]);
    }
    size_t cut = strlen(exe);
    while (cut > 0 && !host_is_sep(exe[cut - 1])) cut--;
    host_home = malloc(cut + 1);
    if (host_home == NULL) tcs_fault("out-of-memory");
    memcpy(host_home, exe, cut);
    host_home[cut] = 0;
    return host_home;
}

TcsString *tcs_host_tcshost_tool_file(TcsString *name)
{
    const char *home = host_tool_home();
    char *rel = host_cstring(name);
    size_t n = strlen(home) + strlen(rel) + 1;
    char *full = malloc(n);
    if (full == NULL) tcs_fault("out-of-memory");
    snprintf(full, n, "%s%s", home, rel);
    free(rel);
    TcsString *text = host_read_path(full);
    free(full);
    return text;
}

TcsString *tcs_host_tcshost_clock(void)
{
    time_t now = time(NULL);
    struct tm *t = localtime(&now);
    char buf[16];
    snprintf(buf, sizeof buf, "%02d:%02d:%02d", t->tm_hour, t->tm_min, t->tm_sec);
    return host_cstr(buf);
}

void tcs_host_tcshost_sleep(int32_t milliseconds)
{
#ifdef _WIN32
    Sleep((DWORD)milliseconds);
#else
    struct timespec ts;
    ts.tv_sec = milliseconds / 1000;
    ts.tv_nsec = (long)(milliseconds % 1000) * 1000000L;
    nanosleep(&ts, NULL);
#endif
}

void tcs_host_tcshost_set_exit_code(int32_t code)
{
    host_exit_code = code;
}

void tcs_host_tcshost_set_done(void)
{
    host_done = 1;
}

/* TCS_HEAP=1 なら、段ごとの managed heap の大きさの最大を stderr に出す (entry の間が GC の frame の境界) */
int main(int argc, char **argv)
{
    host_argc = argc;
    host_argv = argv;
#ifdef _WIN32
    /* .NET の tcs と同じく改行を変えずに書く (text mode は \n を \r\n にする) */
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
    _setmode(_fileno(stderr), _O_BINARY);
#endif
    tcs_lib_init();
    const char *heap = getenv("TCS_HEAP");
    size_t peak = 0;
    int steps = 0;
    tcs_entry_TcsSelf_Cli_Start();
    while (!host_done)
    {
        tcs_entry_TcsSelf_Cli_Step();
        steps++;
        size_t now = tcs_lib_heap_bytes();
        if (now > peak) peak = now;
    }
    if (heap != NULL && heap[0] == '1')
        fprintf(stderr, "steps %d, peak managed heap between steps %zu bytes\n", steps, peak);
    fflush(stdout);
    return host_exit_code;
}
