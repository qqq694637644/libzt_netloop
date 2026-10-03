#include "common.hpp"

#include "ZeroTierSockets.h"

#include <asio.hpp>

#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace netloop {
namespace {

constexpr std::size_t kBufferSize = 64 * 1024;
constexpr auto kSlowIoThreshold = std::chrono::milliseconds(100);

int zt_socket_error(int fd)
{
    const int error = zts_get_last_socket_error(fd);
    return error >= 0 ? error : ZTS_ERR_NO_RESULT;
}

asio::ip::tcp native_protocol(SOCKET socket)
{
    sockaddr_storage address {};
    int length = static_cast<int>(sizeof(address));
    if (getsockname(socket, reinterpret_cast<sockaddr*>(&address), &length) == SOCKET_ERROR) {
        throw std::runtime_error(
            "getsockname failed before Asio adoption, WSA=" + std::to_string(WSAGetLastError()));
    }

    if (address.ss_family == AF_INET) {
        return asio::ip::tcp::v4();
    }
    if (address.ss_family == AF_INET6) {
        return asio::ip::tcp::v6();
    }
    throw std::runtime_error(
        "unsupported native socket family: " + std::to_string(address.ss_family));
}

class AsioRelay {
public:
    AsioRelay(
        asio::io_context& io,
        asio::ip::tcp::socket& native_socket,
        int zt_socket,
        Logger& log,
        std::uint64_t connection_id)
        : io_(io)
        , work_(asio::make_work_guard(io))
        , native_(native_socket)
        , zt_socket_(zt_socket)
        , log_(log)
        , connection_id_(connection_id)
    {
    }

    void run()
    {
        try {
            zt_reader_ = std::thread(&AsioRelay::zt_reader_loop, this);
            zt_writer_ = std::thread(&AsioRelay::zt_writer_loop, this);
        } catch (const std::exception& ex) {
            request_stop(std::string("unable to start relay worker: ") + ex.what());
        }

        start_native_read();
        io_.run();

        stopping_.store(true);
        state_cv_.notify_all();

        if (zt_reader_.joinable()) {
            zt_reader_.join();
        }
        if (zt_writer_.joinable()) {
            zt_writer_.join();
        }
    }

private:
    void start_native_read()
    {
        if (stopping_.load()) {
            return;
        }

        native_.async_read_some(
            asio::buffer(native_read_buffer_),
            [this](const asio::error_code& error, std::size_t bytes) {
                if (stopping_.load()) {
                    return;
                }

                if (error == asio::error::eof || (!error && bytes == 0)) {
                    {
                        std::lock_guard<std::mutex> lock(state_mutex_);
                        native_eof_pending_ = true;
                    }
                    state_cv_.notify_all();
                    return;
                }

                if (error) {
                    request_stop("native async_read failed: " + error.message());
                    return;
                }

                {
                    std::lock_guard<std::mutex> lock(state_mutex_);
                    zt_write_size_ = bytes;
                }
                state_cv_.notify_all();
            });
    }

    bool zt_write_all(const char* data, std::size_t size)
    {
        std::size_t offset = 0;
        while (offset < size && !stopping_.load()) {
            const std::size_t requested = size - offset;
            const auto started = std::chrono::steady_clock::now();
            const ssize_t written = zts_write(zt_socket_, data + offset, requested);
            const auto elapsed = std::chrono::steady_clock::now() - started;

            if (written <= 0) {
                if (!stopping_.load()) {
                    request_stop(
                        "zts_write failed, socket_error="
                        + std::to_string(zt_socket_error(zt_socket_)));
                }
                return false;
            }

            if (elapsed >= kSlowIoThreshold
                || static_cast<std::size_t>(written) < requested) {
                log_.info(
                    "conn=" + std::to_string(connection_id_)
                    + " flow dir=native->zt stage=zts_write requested="
                    + std::to_string(requested) + " written=" + std::to_string(written)
                    + " elapsed_ms="
                    + std::to_string(
                        std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count()));
            }

            offset += static_cast<std::size_t>(written);
        }
        return offset == size;
    }

    void zt_writer_loop()
    {
        for (;;) {
            std::size_t bytes = 0;
            bool native_eof = false;

            {
                std::unique_lock<std::mutex> lock(state_mutex_);
                state_cv_.wait(lock, [this]() {
                    return stopping_.load() || zt_write_size_ != 0 || native_eof_pending_;
                });

                if (stopping_.load()) {
                    return;
                }

                if (zt_write_size_ != 0) {
                    bytes = zt_write_size_;
                } else if (native_eof_pending_) {
                    native_eof_pending_ = false;
                    native_eof = true;
                }
            }

            if (bytes != 0) {
                if (!zt_write_all(native_read_buffer_.data(), bytes)) {
                    return;
                }

                {
                    std::lock_guard<std::mutex> lock(state_mutex_);
                    zt_write_size_ = 0;
                }

                asio::post(io_, [this]() {
                    start_native_read();
                });
                continue;
            }

            if (native_eof) {
                const int rc = zts_bsd_shutdown(zt_socket_, ZTS_SHUT_WR);
                if (rc != ZTS_ERR_OK && !stopping_.load()) {
                    request_stop(
                        "zts shutdown(write) failed, socket_error="
                        + std::to_string(zt_socket_error(zt_socket_)));
                    return;
                }
                complete_native_to_zt();
                return;
            }
        }
    }

    void zt_reader_loop()
    {
        std::array<char, kBufferSize> buffer {};

        while (!stopping_.load()) {
            const ssize_t received = zts_read(zt_socket_, buffer.data(), buffer.size());
            if (received == 0) {
                asio::post(io_, [this]() {
                    if (!stopping_.load() && native_.is_open()) {
                        asio::error_code error;
                        native_.shutdown(asio::ip::tcp::socket::shutdown_send, error);
                    }
                    complete_zt_to_native();
                });
                return;
            }

            if (received < 0) {
                if (!stopping_.load()) {
                    request_stop(
                        "zts_read failed, socket_error="
                        + std::to_string(zt_socket_error(zt_socket_)));
                }
                return;
            }

            const std::size_t received_size = static_cast<std::size_t>(received);
            auto payload = std::make_shared<std::vector<char>>(
                buffer.data(), buffer.data() + received_size);

            {
                std::lock_guard<std::mutex> lock(state_mutex_);
                native_write_done_ = false;
                native_write_error_.clear();
            }

            const auto queued_at = std::chrono::steady_clock::now();
            asio::post(io_, [this, payload, queued_at]() {
                if (stopping_.load()) {
                    signal_native_write(
                        asio::error::make_error_code(asio::error::operation_aborted));
                    return;
                }

                asio::async_write(
                    native_,
                    asio::buffer(*payload),
                    [this, payload, queued_at](
                        const asio::error_code& error,
                        std::size_t written) {
                        const auto elapsed = std::chrono::steady_clock::now() - queued_at;

                        if (!error && elapsed >= kSlowIoThreshold) {
                            log_.info(
                                "conn=" + std::to_string(connection_id_)
                                + " flow dir=zt->native stage=asio_async_write written="
                                + std::to_string(written) + " elapsed_ms="
                                + std::to_string(
                                    std::chrono::duration_cast<std::chrono::milliseconds>(elapsed)
                                        .count()));
                        }

                        signal_native_write(error);

                        if (error && !stopping_.load()) {
                            request_stop("native async_write failed: " + error.message());
                        }
                    });
            });

            std::unique_lock<std::mutex> lock(state_mutex_);
            state_cv_.wait(lock, [this]() {
                return stopping_.load() || native_write_done_;
            });

            if (stopping_.load()) {
                return;
            }
            if (native_write_error_) {
                return;
            }
        }
    }

    void signal_native_write(const asio::error_code& error)
    {
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            native_write_error_ = error;
            native_write_done_ = true;
        }
        state_cv_.notify_all();
    }

    void complete_native_to_zt()
    {
        bool all_done = false;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            native_to_zt_done_ = true;
            all_done = zt_to_native_done_;
        }
        if (all_done) {
            finish_normally();
        }
    }

    void complete_zt_to_native()
    {
        bool all_done = false;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            zt_to_native_done_ = true;
            all_done = native_to_zt_done_;
        }
        if (all_done) {
            finish_normally();
        }
    }

    void finish_normally()
    {
        if (finish_posted_.exchange(true)) {
            return;
        }

        asio::post(io_, [this]() {
            asio::error_code error;
            if (native_.is_open()) {
                native_.close(error);
            }
            work_.reset();
        });
        state_cv_.notify_all();
    }

    void request_stop(const std::string& reason)
    {
        if (stopping_.exchange(true)) {
            return;
        }

        log_.error("conn=" + std::to_string(connection_id_) + " relay failure: " + reason);
        state_cv_.notify_all();

        zts_bsd_shutdown(zt_socket_, ZTS_SHUT_RDWR);

        asio::post(io_, [this]() {
            asio::error_code error;
            if (native_.is_open()) {
                native_.cancel(error);
                native_.close(error);
            }
            work_.reset();
        });
    }

    asio::io_context& io_;
    asio::executor_work_guard<asio::io_context::executor_type> work_;
    asio::ip::tcp::socket& native_;
    int zt_socket_;
    Logger& log_;
    std::uint64_t connection_id_;

    std::array<char, kBufferSize> native_read_buffer_ {};

    std::mutex state_mutex_;
    std::condition_variable state_cv_;
    std::atomic_bool stopping_ { false };
    std::atomic_bool finish_posted_ { false };

    std::size_t zt_write_size_ = 0;
    bool native_eof_pending_ = false;
    bool native_to_zt_done_ = false;
    bool zt_to_native_done_ = false;

    bool native_write_done_ = false;
    asio::error_code native_write_error_;

    std::thread zt_reader_;
    std::thread zt_writer_;
};

} // namespace

void relay_native_and_zt(
    SOCKET native_socket,
    int zt_socket,
    Logger& log,
    std::uint64_t connection_id)
{
    asio::io_context io;
    auto native = std::make_unique<asio::ip::tcp::socket>(io);
    bool asio_owns_native = false;

    try {
        const asio::ip::tcp protocol = native_protocol(native_socket);
        asio::error_code error;
        native->assign(protocol, native_socket, error);
        if (error) {
            throw std::runtime_error("Asio socket assign failed: " + error.message());
        }
        asio_owns_native = true;

        AsioRelay relay(io, *native, zt_socket, log, connection_id);
        relay.run();
    } catch (const std::exception& ex) {
        log.error(
            "conn=" + std::to_string(connection_id)
            + " Asio relay setup failure: " + ex.what());
        if (!asio_owns_native) {
            closesocket(native_socket);
        } else if (native->is_open()) {
            asio::error_code error;
            native->close(error);
        }
        zts_bsd_shutdown(zt_socket, ZTS_SHUT_RDWR);
    }

    zts_close(zt_socket);
    log.info("conn=" + std::to_string(connection_id) + " closed");
}

} // namespace netloop
