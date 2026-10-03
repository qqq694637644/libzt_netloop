#include "common.hpp"

#include "uv.h"

namespace netloop {

bool create_native_socket_pair(SOCKET sockets[2], std::string& error)
{
    uv_os_sock_t pair[2] = { INVALID_SOCKET, INVALID_SOCKET };
    // libhv uses wepoll (IOCP-backed) by default on Windows. The endpoint that
    // libhv monitors therefore has to be created as an OVERLAPPED socket.
    // uv_socketpair() maps UV_NONBLOCK_PIPE to WSA_FLAG_OVERLAPPED on Windows.
    // Keep the helper endpoint in its normal blocking mode for the Pylon-style
    // recv/send helper thread.
    const int rc = uv_socketpair(SOCK_STREAM, 0, pair, UV_NONBLOCK_PIPE, 0);
    if (rc != 0) {
        error = uv_strerror(rc);
        return false;
    }

    sockets[0] = pair[0];
    sockets[1] = pair[1];
    return true;
}

} // namespace netloop
