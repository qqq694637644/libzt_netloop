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
        << "zt_netloop_server --network <hex> --state-dir <dir> [options]\n\n"
        << "Options:\n"
        << "  --zt-port <port>       ZeroTier listen port (default 42042)\n"
        << "  --bind-host <zt-ip>    ZeroTier address; auto-select when omitted\n"
        << "  --forward-host <host>  Local SOCKS endpoint (default 127.0.0.1)\n"
        << "  --forward-port <port>  Local SOCKS endpoint port (default 10808)\n"
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
            std::cout << "zt_netloop_server " << NETLOOP_VERSION << "\n";
            return 0;
        }

        const std::uint64_t network_id =
            netloop::parse_hex_u64(netloop::require_arg(args, "--network"));
        const std::filesystem::path state_dir = netloop::require_arg(args, "--state-dir");
        const std::uint16_t zt_port =
            netloop::parse_port(netloop::optional_arg(args, "--zt-port", "42042"), "ZeroTier port");
        const std::string forward_host =
            netloop::optional_arg(args, "--forward-host", "127.0.0.1");
        const std::uint16_t forward_port =
            netloop::parse_port(netloop::optional_arg(args, "--forward-port", "10808"), "forward port");
        const std::filesystem::path status_file =
            netloop::optional_arg(args, "--status-file", "");
        const std::filesystem::path log_file =
            netloop::optional_arg(args, "--log-file", "");
        const auto timeout =
            std::chrono::seconds(std::stoul(netloop::optional_arg(args, "--timeout", "120")));

        netloop::Logger log("server", log_file);
        netloop::WinsockRuntime winsock;
        log.info("starting");
        const auto network =
            netloop::start_libzt_network(network_id, state_dir, timeout, log);

        std::string bind_host = netloop::optional_arg(args, "--bind-host", "");
        if (bind_host.empty()) {
            bind_host = !network.ipv4.empty() ? network.ipv4 : network.ipv6;
        }
        if (bind_host.empty()) {
            throw std::runtime_error("network has no usable assigned address");
        }

        const int zt_family = zts_util_get_ip_family(bind_host.c_str());
        if (zt_family != ZTS_AF_INET && zt_family != ZTS_AF_INET6) {
            throw std::runtime_error("bind host is not an IPv4/IPv6 address: " + bind_host);
        }

        const int listener = zts_socket(zt_family, ZTS_SOCK_STREAM, 0);
        if (listener < 0) {
            throw std::runtime_error("zts_socket(listener) failed, api_rc=" + std::to_string(listener));
        }
        const int reuse_rc = zts_set_reuse_addr(listener, 1);
        if (reuse_rc != ZTS_ERR_OK) {
            const int socket_error = netloop::zt_socket_error(listener);
            zts_close(listener);
            throw std::runtime_error(
                "zts_set_reuse_addr(listener) failed, api_rc=" + std::to_string(reuse_rc)
                + ", socket_error=" + std::to_string(socket_error));
        }
        const int bind_rc = zts_bind(listener, bind_host.c_str(), zt_port);
        if (bind_rc != ZTS_ERR_OK) {
            const int socket_error = netloop::zt_socket_error(listener);
            zts_close(listener);
            throw std::runtime_error(
                "zts_bind failed, api_rc=" + std::to_string(bind_rc)
                + ", socket_error=" + std::to_string(socket_error));
        }
        const int listen_rc = zts_listen(listener, 128);
        if (listen_rc != ZTS_ERR_OK) {
            const int socket_error = netloop::zt_socket_error(listener);
            zts_close(listener);
            throw std::runtime_error(
                "zts_listen failed, api_rc=" + std::to_string(listen_rc)
                + ", socket_error=" + std::to_string(socket_error));
        }

        log.info(
            "ready, zt=" + bind_host + ":" + std::to_string(zt_port)
            + ", forward=" + forward_host + ":" + std::to_string(forward_port));

        netloop::write_status_json(
            status_file,
            {
                { "phase", "ready" },
                { "role", "server" },
                { "network_id", netloop::hex_u64(network.network_id) },
                { "node_id", netloop::hex_u64(network.node_id) },
                { "ipv4", network.ipv4 },
                { "ipv6", network.ipv6 },
                { "zt_listen_host", bind_host },
                { "forward_host", forward_host },
            },
            {
                { "zt_port", zt_port },
                { "forward_port", forward_port },
            });

        std::atomic_uint64_t next_id { 1 };
        for (;;) {
            char remote_ip[ZTS_INET6_ADDRSTRLEN] = {};
            unsigned short remote_port = 0;
            const int accepted =
                zts_accept(listener, remote_ip, sizeof(remote_ip), &remote_port);
            if (accepted < 0) {
                log.error(
                    "zts_accept failed, api_rc=" + std::to_string(accepted)
                    + ", socket_error=" + std::to_string(netloop::zt_socket_error(listener)));
                zts_util_delay(100);
                continue;
            }

            const std::uint64_t connection_id = next_id.fetch_add(1);
            log.info(
                "conn=" + std::to_string(connection_id) + " accepted from " + remote_ip + ":"
                + std::to_string(remote_port));
            std::thread(
                [accepted, forward_host, forward_port, connection_id, &log]() {
                    try {
                        netloop::configure_zt_stream_socket(accepted);
                        SOCKET target = netloop::native_connect(forward_host, forward_port);
                        log.info(
                            "conn=" + std::to_string(connection_id) + " forward connected "
                            + forward_host + ":" + std::to_string(forward_port));
                        netloop::relay_native_and_zt(target, accepted, log, connection_id);
                    } catch (const std::exception& ex) {
                        log.error(
                            "conn=" + std::to_string(connection_id)
                            + " forward failure: " + ex.what());
                        zts_close(accepted);
                    }
                })
                .detach();
        }
    } catch (const std::exception& ex) {
        std::cerr << "fatal: " << ex.what() << std::endl;
        return 1;
    }
}
