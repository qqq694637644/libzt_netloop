#include "common.hpp"

#include "uv.h"

namespace netloop {

bool create_native_socket_pair(SOCKET sockets[2], std::string& error)
{
    uv_os_sock_t pair[2] = { INVALID_SOCKET, INVALID_SOCKET };
    // libhv is built with its WSAPoll backend for this bridge. hio_get() makes
    // the endpoint it owns non-blocking, while the helper endpoint stays in the
    // normal blocking mode expected by the Pylon-style helper thread.
    const int rc = uv_socketpair(SOCK_STREAM, 0, pair, 0, 0);
    if (rc != 0) {
        error = uv_strerror(rc);
        return false;
    }

    sockets[0] = pair[0];
    sockets[1] = pair[1];
    return true;
}

} // namespace netloop
