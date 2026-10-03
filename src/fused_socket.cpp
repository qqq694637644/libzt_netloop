#include "common.hpp"

#include "ZeroTierSockets.h"
#include "hv/hloop.h"

#include <array>
#include <atomic>
#include <exception>
#include <limits>
#include <string>
#include <thread>

namespace netloop {
namespace {

// This bridge is intentionally modeled after ZeroTier Pylon's zts_fused_socket,
// fused_socket_tx_helper, and fused_socket_rx_helper design:
//   libzt fd <-> helper threads <-> socketpair <-> ordinary OS socket.
// On Windows uv_socketpair() replaces Pylon's POSIX socketpair(), while libhv
// owns the ordinary-socket bidirectional relay and write backpressure.
constexpr int kPollTimeoutMs = 500;
constexpr std::size_t kBufferSize = 16 * 1024;

struct FusedSocketContext {
    std::atomic_bool should_stop { false };
    std::atomic_bool fused_closed { false };
    std::atomic_bool logged_zt_to_os { false };
    std::atomic_bool logged_os_to_zt { false };
    SOCKET fd_zan = INVALID_SOCKET; // OS socket exposed to libhv
    SOCKET fd_int = INVALID_SOCKET; // helper side of the socketpair
    int fd_zts = -1;                // libzt socket
    Logger* log = nullptr;
    std::uint64_t connection_id = 0;
};

struct HvIoContext {
    Logger* log = nullptr;
    std::uint64_t connection_id = 0;
    const char* direction = nullptr;
    std::atomic_bool logged { false };
};

std::string conn_prefix(const FusedSocketContext& conn)
{
    return "conn=" + std::to_string(conn.connection_id) + " ";
}

int zt_socket_error(int fd)
{
    const int error = zts_get_last_socket_error(fd);
    return error >= 0 ? error : ZTS_ERR_NO_RESULT;
}

void stop_fused_socket(FusedSocketContext& conn)
{
    conn.should_stop.store(true);
    if (conn.fd_int != INVALID_SOCKET) {
        shutdown(conn.fd_int, SD_BOTH);
    }
}

void fail_fused_socket(FusedSocketContext& conn, const std::string& message)
{
    if (!conn.fused_closed.exchange(true) && conn.log != nullptr) {
        conn.log->error(conn_prefix(conn) + message);
    }
    stop_fused_socket(conn);
}

bool native_send_all(FusedSocketContext& conn, const char* data, std::size_t size)
{
    std::size_t offset = 0;
    while (offset < size && !conn.should_stop.load()) {
        const std::size_t remaining_size = size - offset;
        const int remaining = static_cast<int>(
            remaining_size > static_cast<std::size_t>(std::numeric_limits<int>::max())
                ? std::numeric_limits<int>::max()
                : remaining_size);
        const int sent = send(conn.fd_int, data + offset, remaining, 0);
        if (sent == SOCKET_ERROR || sent == 0) {
            if (!conn.should_stop.load()) {
                fail_fused_socket(
                    conn,
                    "fused zt->os send failed, WSA=" + std::to_string(WSAGetLastError()));
            }
            return false;
        }
        offset += static_cast<std::size_t>(sent);
    }
    return offset == size;
}

void hio_write_upstream_logged(hio_t* io, void* buf, int bytes)
{
    auto* context = static_cast<HvIoContext*>(hio_context(io));
    if (context != nullptr && bytes > 0 && !context->logged.exchange(true)
        && context->log != nullptr) {
        context->log->info(
            "conn=" + std::to_string(context->connection_id) + " libhv "
            + context->direction + " first_bytes=" + std::to_string(bytes));
    }
    hio_write_upstream(io, buf, bytes);
}

bool zt_write_all(FusedSocketContext& conn, const char* data, std::size_t size)
{
    std::size_t offset = 0;
    while (offset < size && !conn.should_stop.load()) {
        const ssize_t written = zts_write(conn.fd_zts, data + offset, size - offset);
        if (written <= 0) {
            if (!conn.should_stop.load()) {
                fail_fused_socket(
                    conn,
                    "fused os->zt write failed, socket_error="
                        + std::to_string(zt_socket_error(conn.fd_zts)));
            }
            return false;
        }
        offset += static_cast<std::size_t>(written);
    }
    return offset == size;
}

// Pylon fused_socket_tx_helper equivalent: retrieve data from libzt and feed it
// into the OS socketpair.
void fused_socket_tx_helper(FusedSocketContext* conn)
{
    std::array<char, kBufferSize> buffer {};

    while (!conn->should_stop.load() && !conn->fused_closed.load()) {
        zts_pollfd fd {};
        fd.fd = conn->fd_zts;
        fd.events = ZTS_POLLIN;

        const int rc = zts_bsd_poll(&fd, 1, kPollTimeoutMs);
        if (rc < 0) {
            if (!conn->should_stop.load()) {
                fail_fused_socket(
                    *conn,
                    "fused zt poll failed, socket_error="
                        + std::to_string(zt_socket_error(conn->fd_zts)));
            }
            break;
        }
        if (rc == 0) {
            continue;
        }

        if ((fd.revents & (ZTS_POLLERR | ZTS_POLLNVAL)) != 0) {
            if (!conn->should_stop.load()) {
                fail_fused_socket(
                    *conn,
                    "fused zt poll error, revents=" + std::to_string(fd.revents)
                        + ", socket_error=" + std::to_string(zt_socket_error(conn->fd_zts)));
            }
            break;
        }

        if ((fd.revents & (ZTS_POLLIN | ZTS_POLLHUP)) == 0) {
            continue;
        }

        const ssize_t received = zts_read(conn->fd_zts, buffer.data(), buffer.size());
        if (received == 0) {
            // Preserve the useful half-close behavior: the libzt peer finished
            // sending, so only close the helper's send direction. libhv will see
            // EOF on fd_zan and finish the ordinary-socket relay cleanly.
            shutdown(conn->fd_int, SD_SEND);
            break;
        }
        if (received < 0) {
            if (!conn->should_stop.load()) {
                fail_fused_socket(
                    *conn,
                    "fused zt read failed, socket_error="
                        + std::to_string(zt_socket_error(conn->fd_zts)));
            }
            break;
        }

        if (!conn->logged_zt_to_os.exchange(true) && conn->log != nullptr) {
            conn->log->info(
                conn_prefix(*conn) + "fused zt->os first_bytes="
                + std::to_string(received));
        }

        if (!native_send_all(*conn, buffer.data(), static_cast<std::size_t>(received))) {
            break;
        }
    }
}

// Pylon fused_socket_rx_helper equivalent: retrieve data from the OS socketpair
// and feed it into libzt.
void fused_socket_rx_helper(FusedSocketContext* conn)
{
    std::array<char, kBufferSize> buffer {};

    while (!conn->should_stop.load() && !conn->fused_closed.load()) {
        const int received =
            recv(conn->fd_int, buffer.data(), static_cast<int>(buffer.size()), 0);
        if (received == 0) {
            // The ordinary-socket side finished sending. Keep the reverse
            // direction alive while telling libzt that no more bytes follow.
            zts_bsd_shutdown(conn->fd_zts, ZTS_SHUT_WR);
            break;
        }
        if (received == SOCKET_ERROR) {
            if (!conn->should_stop.load()) {
                fail_fused_socket(
                    *conn,
                    "fused os recv failed, WSA=" + std::to_string(WSAGetLastError()));
            }
            break;
        }

        if (!conn->logged_os_to_zt.exchange(true) && conn->log != nullptr) {
            conn->log->info(
                conn_prefix(*conn) + "fused os->zt first_bytes="
                + std::to_string(received));
        }

        if (!zt_write_all(*conn, buffer.data(), static_cast<std::size_t>(received))) {
            break;
        }
    }
}

bool zts_fused_socket(FusedSocketContext& conn)
{
    SOCKET sockets[2] = { INVALID_SOCKET, INVALID_SOCKET };
    std::string error;
    if (!create_native_socket_pair(sockets, error)) {
        if (conn.log != nullptr) {
            conn.log->error(
                conn_prefix(conn) + "uv_socketpair failed: " + error);
        }
        return false;
    }

    conn.fd_zan = sockets[0];
    conn.fd_int = sockets[1];
    return true;
}

bool socket_to_hv_fd(SOCKET socket, int& fd)
{
    const auto value = static_cast<unsigned long long>(socket);
    if (value > static_cast<unsigned long long>(std::numeric_limits<int>::max())) {
        return false;
    }
    fd = static_cast<int>(value);
    return true;
}

bool run_libhv_relay(
    SOCKET native_socket,
    SOCKET fused_socket,
    Logger& log,
    std::uint64_t connection_id)
{
    int native_fd = -1;
    int fused_fd = -1;
    if (!socket_to_hv_fd(native_socket, native_fd)
        || !socket_to_hv_fd(fused_socket, fused_fd)) {
        log.error(
            "conn=" + std::to_string(connection_id)
            + " libhv cannot represent a Windows SOCKET larger than INT_MAX");
        closesocket(native_socket);
        closesocket(fused_socket);
        return false;
    }

    hloop_t* loop = hloop_new(HLOOP_FLAG_QUIT_WHEN_NO_ACTIVE_EVENTS);
    if (loop == nullptr) {
        log.error("conn=" + std::to_string(connection_id) + " hloop_new failed");
        closesocket(native_socket);
        closesocket(fused_socket);
        return false;
    }

    hio_t* native_io = hio_get(loop, native_fd);
    hio_t* fused_io = hio_get(loop, fused_fd);
    if (native_io == nullptr || fused_io == nullptr) {
        log.error("conn=" + std::to_string(connection_id) + " hio_get failed");
        if (native_io != nullptr) {
            hio_close(native_io);
        } else {
            closesocket(native_socket);
        }
        if (fused_io != nullptr) {
            hio_close(fused_io);
        } else {
            closesocket(fused_socket);
        }
        hloop_run(loop);
        hloop_free(&loop);
        return false;
    }

    HvIoContext native_context { &log, connection_id, "native->fused" };
    HvIoContext fused_context { &log, connection_id, "fused->native" };
    hio_set_context(native_io, &native_context);
    hio_set_context(fused_io, &fused_context);

    // This is libhv's own tcp-proxy wiring: both sides use its queued writes,
    // backpressure and close propagation instead of our previous hand-written
    // pair of blocking recv/send loops.
    hio_setup_upstream(native_io, fused_io);
    hio_setcb_read(native_io, hio_write_upstream_logged);
    hio_setcb_read(fused_io, hio_write_upstream_logged);
    hio_setcb_close(native_io, hio_close_upstream);
    hio_setcb_close(fused_io, hio_close_upstream);
    hio_read_upstream(native_io);

    hloop_run(loop);
    hloop_free(&loop);
    return true;
}

} // namespace

void relay_native_and_zt(
    SOCKET native_socket,
    int zt_socket,
    Logger& log,
    std::uint64_t connection_id)
{
    FusedSocketContext conn;
    conn.fd_zts = zt_socket;
    conn.log = &log;
    conn.connection_id = connection_id;

    if (!zts_fused_socket(conn)) {
        closesocket(native_socket);
        zts_close(zt_socket);
        return;
    }

    std::thread tx;
    std::thread rx;
    try {
        tx = std::thread(fused_socket_tx_helper, &conn);
        rx = std::thread(fused_socket_rx_helper, &conn);
    } catch (const std::exception& ex) {
        log.error(
            conn_prefix(conn) + "unable to start fused socket helpers: " + ex.what());
        stop_fused_socket(conn);
        if (tx.joinable()) {
            tx.join();
        }
        if (rx.joinable()) {
            rx.join();
        }
        closesocket(native_socket);
        closesocket(conn.fd_zan);
        closesocket(conn.fd_int);
        zts_close(conn.fd_zts);
        return;
    }

    // libhv takes ownership of native_socket and fd_zan. The helper side and
    // libzt socket remain owned by this function.
    run_libhv_relay(native_socket, conn.fd_zan, log, connection_id);
    conn.fd_zan = INVALID_SOCKET;

    stop_fused_socket(conn);
    if (tx.joinable()) {
        tx.join();
    }
    if (rx.joinable()) {
        rx.join();
    }

    if (conn.fd_int != INVALID_SOCKET) {
        closesocket(conn.fd_int);
        conn.fd_int = INVALID_SOCKET;
    }
    if (conn.fd_zts >= 0) {
        zts_close(conn.fd_zts);
        conn.fd_zts = -1;
    }

    log.info("conn=" + std::to_string(connection_id) + " closed");
}

} // namespace netloop
