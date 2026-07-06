#pragma once
// POSIX-socket compatibility for serve.c and its smoke test. Callers are
// written against BSD sockets with int fds; this header maps that surface
// onto Winsock. SOCKET handles are stored as int — Windows socket handles
// stay far below INT_MAX in practice, and keeping int fds means ServeState
// needs no platform forks.
#include <errno.h>
#include <stdbool.h>

#ifdef _WIN32

#include <winsock2.h>
#include <ws2tcpip.h>

#ifdef _MSC_VER
typedef SSIZE_T ssize_t;
#endif
typedef ULONG nfds_t;
#define poll WSAPoll
#ifndef MSG_NOSIGNAL
#define MSG_NOSIGNAL 0
#endif
// Sockets here are always non-blocking, so a plain MSG_PEEK recv already
// behaves like MSG_DONTWAIT.
#ifndef MSG_DONTWAIT
#define MSG_DONTWAIT 0
#endif

static inline bool sock_startup(void) {
  WSADATA d;
  return WSAStartup(MAKEWORD(2, 2), &d) == 0;
}
static inline void sock_close(int fd) { closesocket((SOCKET)fd); }
static inline bool sock_would_block(void) {
  return WSAGetLastError() == WSAEWOULDBLOCK;
}
static inline int sock_set_nonblocking(int fd) {
  u_long nb = 1;
  return ioctlsocket((SOCKET)fd, FIONBIO, &nb);
}

#else // POSIX

#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <sys/socket.h>
#include <unistd.h>

static inline bool sock_startup(void) { return true; }
static inline void sock_close(int fd) { close(fd); }
static inline bool sock_would_block(void) {
  return errno == EAGAIN || errno == EWOULDBLOCK;
}
static inline int sock_set_nonblocking(int fd) {
  int flags = fcntl(fd, F_GETFL, 0);
  if (flags < 0)
    return -1;
  return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

#endif
