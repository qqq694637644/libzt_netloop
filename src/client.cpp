#include "common.hpp"

#include "ZeroTierSockets.h"

#include <atomic>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <thread>

namespace {

void usage()
{
    std::cout
        << "zt_netloop_client --network <hex> --state-dir <dir> "
           "--remote-host <zt-ip> [options]\n\n"
        << "Options:\n"
        << "  --remote-port <port>   ZeroTier server port (default 42042)\n"
        << "  --listen-host <host>   Local listen address (default 127.0.0.1)\n"
        << "  --listen-port <port>   Local listen port (default 1080)\n"
        << "  --status-file <path>   Write machine-readable readiness state\n"
        << "  --log-file <path>      Append diagnostic logs\n"
        << "  --timeout <seconds>    ZeroTier startup timeout (default 120)\n";
}

} // namespace

int main(int argc, char** argv)
{
    try {
        const auto args = netloop::parse_cli(argc, argv);
        if (args.contains("--help")) {
            usage();
            return 0;
        }
        if (args.contains("--version")) {
            std::cout << "zt_netloop_client " << NETLOOP_VERSION << "\n";
            return 0;
        }

        const std::uint64_t network_id =
            netloop::parse_hex_u64(netloop::require_arg(args, "--network"));
        const std::filesystem::path state_dir = netloop::require_arg(args, "--state-dir");
        const std::string remote_host = netloop::require_arg(args, "--remote-host");
        const std::uint16_t remote_port =
            netloop::parse_port(netloop::optional_arg(args, "--remote-port", "42042"), "remote port");
        const std::string listen_host =
            netloop::optional_arg(args, "--listen-host", "127.0.0.1");
        const std::uint16_t listen_port =
            netloop::parse_port(netloop::optional_arg(args, "--listen-port", "1080"), "listen port");
        const std::filesystem::path status_file =
            netloop::optional_arg(args, "--status-file", "");
        const std::filesystem::path log_file =
            netloop::optional_arg(args, "--log-file", "");
        const auto timeout =
            std::chrono::seconds(std::stoul(netloop::optional_arg(args, "--timeout", "120")));

        netloop::Logger log("client", log_file);
        netloop::WinsockRuntime winsock;
        log.info("starting");
        const auto network =
            netloop::start_libzt_network(network_id, state_dir, timeout, log);

        const int zt_family = zts_util_get_ip_family(remote_host.c_str());
        if (zt_family != ZTS_AF_INET && zt_family != ZTS_AF_INET6) {
            throw std::runtime_error("remote host is not an IPv4/IPv6 address: " + remote_host);
        }

        const SOCKET listener = netloop::native_listen(listen_host, listen_port);
        log.info(
            "ready, local=" + listen_host + ":" + std::to_string(listen_port)
            + ", remote=" + remote_host + ":" + std::to_string(remote_port));

        netloop::write_status_json(
            status_file,
            {
                { "phase", "ready" },
                { "role", "client" },
                { "network_id", netloop::hex_u64(network.network_id) },
                { "node_id", netloop::hex_u64(network.node_id) },
                { "ipv4", network.ipv4 },
                { "ipv6", network.ipv6 },
                { "listen_host", listen_host },
                { "remote_host", remote_host },
            },
            {
                { "listen_port", listen_port },
                { "remote_port", remote_port },
            });

        std::atomic_uint64_t next_id { 1 };
        for (;;) {
            SOCKET local = accept(listener, nullptr, nullptr);
            if (local == INVALID_SOCKET) {
                log.error("accept failed, WSA=" + std::to_string(WSAGetLastError()));
                continue;
            }

            const std::uint64_t connection_id = next_id.fetch_add(1);
            std::thread(
                [local, remote_host, remote_port, zt_family, connection_id, &log]() {
                    int zt = zts_socket(zt_family, ZTS_SOCK_STREAM, 0);
                    if (zt < 0) {
                        log.error(
                            "conn=" + std::to_string(connection_id)
                            + " zts_socket failed, zts_errno=" + std::to_string(zts_errno));
                        closesocket(local);
                        return;
                    }

                    log.info(
                        "conn=" + std::to_string(connection_id) + " connecting " + remote_host
                        + ":" + std::to_string(remote_port));
                    if (zts_connect(zt, remote_host.c_str(), remote_port, 0) != ZTS_ERR_OK) {
                        log.error(
                            "conn=" + std::to_string(connection_id)
                            + " zts_connect failed, zts_errno=" + std::to_string(zts_errno));
                        zts_close(zt);
                        closesocket(local);
                        return;
                    }

                    log.info("conn=" + std::to_string(connection_id) + " connected");
                    netloop::relay_native_and_zt(local, zt, log, connection_id);
                })
                .detach();
        }
    } catch (const std::exception& ex) {
        std::cerr << "fatal: " << ex.what() << std::endl;
        return 1;
    }
}
