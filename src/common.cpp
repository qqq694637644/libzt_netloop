#include "common.hpp"

#include "ZeroTierSockets.h"

#include <WS2tcpip.h>

#include <array>
#include <atomic>
#include <algorithm>
#include <ctime>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <thread>

namespace netloop {
namespace {

Logger* g_zt_event_log = nullptr;

std::string timestamp()
{
    const auto now = std::chrono::system_clock::now();
    const std::time_t now_time = std::chrono::system_clock::to_time_t(now);
    std::tm local_tm {};
    localtime_s(&local_tm, &now_time);
    std::ostringstream out;
    out << std::put_time(&local_tm, "%Y-%m-%d %H:%M:%S");
    return out.str();
}

std::string json_escape(const std::string& input)
{
    std::ostringstream out;
    for (const unsigned char ch : input) {
        switch (ch) {
        case '\\':
            out << "\\\\";
            break;
        case '"':
            out << "\\\"";
            break;
        case '\n':
            out << "\\n";
            break;
        case '\r':
            out << "\\r";
            break;
        case '\t':
            out << "\\t";
            break;
        default:
            if (ch < 0x20) {
                out << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                    << static_cast<int>(ch) << std::dec;
            } else {
                out << static_cast<char>(ch);
            }
        }
    }
    return out.str();
}

bool deadline_expired(const std::chrono::steady_clock::time_point& deadline)
{
    return std::chrono::steady_clock::now() >= deadline;
}

std::string network_status_name(int status)
{
    switch (status) {
    case ZTS_NETWORK_STATUS_REQUESTING_CONFIGURATION:
        return "REQUESTING_CONFIGURATION";
    case ZTS_NETWORK_STATUS_OK:
        return "OK";
    case ZTS_NETWORK_STATUS_ACCESS_DENIED:
        return "ACCESS_DENIED";
    case ZTS_NETWORK_STATUS_NOT_FOUND:
        return "NOT_FOUND";
    case ZTS_NETWORK_STATUS_PORT_ERROR:
        return "PORT_ERROR";
    case ZTS_NETWORK_STATUS_CLIENT_TOO_OLD:
        return "CLIENT_TOO_OLD";
    case ZTS_ERR_SOCKET:
        return "ERR_SOCKET";
    case ZTS_ERR_SERVICE:
        return "ERR_SERVICE";
    case ZTS_ERR_ARG:
        return "ERR_ARG";
    case ZTS_ERR_NO_RESULT:
        return "NO_RESULT";
    case ZTS_ERR_GENERAL:
        return "ERR_GENERAL";
    default:
        return "UNKNOWN(" + std::to_string(status) + ")";
    }
}

bool network_status_is_terminal_error(int status)
{
    return status == ZTS_NETWORK_STATUS_ACCESS_DENIED
        || status == ZTS_NETWORK_STATUS_NOT_FOUND
        || status == ZTS_NETWORK_STATUS_PORT_ERROR
        || status == ZTS_NETWORK_STATUS_CLIENT_TOO_OLD;
}

std::string network_wait_detail(std::uint64_t network_id, int status)
{
    const bool ipv4_assigned = zts_addr_is_assigned(network_id, ZTS_AF_INET) == 1;
    const bool ipv6_assigned = zts_addr_is_assigned(network_id, ZTS_AF_INET6) == 1;
    return "status=" + network_status_name(status)
        + ", transport_ready=false"
        + ", ipv4_assigned=" + (ipv4_assigned ? "true" : "false")
        + ", ipv6_assigned=" + (ipv6_assigned ? "true" : "false");
}

std::string assigned_address(std::uint64_t network_id, int family)
{
    if (!zts_addr_is_assigned(network_id, family)) {
        return {};
    }
    char buffer[ZTS_IP_MAX_STR_LEN] = {};
    if (zts_addr_get_str(network_id, family, buffer, sizeof(buffer)) != ZTS_ERR_OK) {
        return {};
    }
    return buffer;
}

std::string peer_role_name(zts_peer_role_t role)
{
    switch (role) {
    case ZTS_PEER_ROLE_LEAF:
        return "LEAF";
    case ZTS_PEER_ROLE_MOON:
        return "MOON";
    case ZTS_PEER_ROLE_PLANET:
        return "PLANET";
    default:
        return "UNKNOWN(" + std::to_string(static_cast<int>(role)) + ")";
    }
}

std::string peer_event_name(int event_code)
{
    switch (event_code) {
    case ZTS_EVENT_PEER_DIRECT:
        return "DIRECT";
    case ZTS_EVENT_PEER_RELAY:
        return "RELAY";
    case ZTS_EVENT_PEER_UNREACHABLE:
        return "UNREACHABLE";
    case ZTS_EVENT_PEER_PATH_DISCOVERED:
        return "PATH_DISCOVERED";
    case ZTS_EVENT_PEER_PATH_DEAD:
        return "PATH_DEAD";
    default:
        return "UNKNOWN(" + std::to_string(event_code) + ")";
    }
}

std::string peer_path_endpoint(zts_path_t& path)
{
    char address[ZTS_INET6_ADDRSTRLEN] = {};
    unsigned short port = 0;
    auto* socket_address = reinterpret_cast<zts_sockaddr*>(&path.address);
    if (zts_util_ntop(
            socket_address,
            sizeof(path.address),
            address,
            sizeof(address),
            &port)
        != ZTS_ERR_OK) {
        return "?";
    }

    if (socket_address->sa_family == ZTS_AF_INET6) {
        return "[" + std::string(address) + "]:" + std::to_string(port);
    }
    return std::string(address) + ":" + std::to_string(port);
}

void on_zts_event(void* message_ptr)
{
    if (g_zt_event_log == nullptr || message_ptr == nullptr) {
        return;
    }

    auto* message = static_cast<zts_event_msg_t*>(message_ptr);
    if (message->event_code < ZTS_EVENT_PEER_DIRECT
        || message->event_code > ZTS_EVENT_PEER_PATH_DEAD
        || message->peer == nullptr) {
        return;
    }

    zts_peer_info_t& peer = *message->peer;
    g_zt_event_log->info(
        "peer=" + hex_u64(peer.peer_id)
        + " transport=" + peer_event_name(message->event_code)
        + " role=" + peer_role_name(peer.role)
        + " latency_ms=" + std::to_string(peer.latency)
        + " path_count=" + std::to_string(peer.path_count));

    const unsigned int path_count =
        std::min(peer.path_count, static_cast<unsigned int>(ZTS_MAX_PEER_NETWORK_PATHS));
    for (unsigned int index = 0; index < path_count; ++index) {
        zts_path_t& path = peer.paths[index];
        std::ostringstream detail;
        detail << "peer=" << hex_u64(peer.peer_id)
               << " path[" << index << "]=" << peer_path_endpoint(path)
               << " latency_ms=" << std::fixed << std::setprecision(1) << path.latency
               << " preferred=" << (path.preferred ? "true" : "false")
               << " expired=" << (path.expired ? "true" : "false");
        g_zt_event_log->info(detail.str());
    }
}

} // namespace

Logger::Logger(std::string role, const std::filesystem::path& log_path)
    : role_(std::move(role))
{
    if (!log_path.empty()) {
        if (log_path.has_parent_path()) {
            std::filesystem::create_directories(log_path.parent_path());
        }
        file_.open(log_path, std::ios::out | std::ios::app);
    }
}

void Logger::info(const std::string& message)
{
    write("INFO", message);
}

void Logger::error(const std::string& message)
{
    write("ERROR", message);
}

void Logger::write(const char* level, const std::string& message)
{
    const std::string line =
        "[" + timestamp() + "] [" + level + "] [" + role_ + "] " + message;
    std::lock_guard<std::mutex> lock(mutex_);
    std::cout << line << std::endl;
    if (file_) {
        file_ << line << std::endl;
        file_.flush();
    }
}

WinsockRuntime::WinsockRuntime()
{
    WSADATA data {};
    const int rc = WSAStartup(MAKEWORD(2, 2), &data);
    if (rc != 0) {
        throw std::runtime_error("WSAStartup failed: " + std::to_string(rc));
    }
}

WinsockRuntime::~WinsockRuntime()
{
    WSACleanup();
}

std::uint64_t parse_hex_u64(const std::string& value)
{
    std::size_t consumed = 0;
    const std::uint64_t result = std::stoull(value, &consumed, 16);
    if (consumed != value.size()) {
        throw std::invalid_argument("invalid hexadecimal value: " + value);
    }
    return result;
}

std::string hex_u64(std::uint64_t value)
{
    std::ostringstream out;
    out << std::hex << std::setfill('0') << std::setw(16) << value;
    return out.str();
}

NetworkStatus start_libzt_network(
    std::uint64_t network_id,
    const std::filesystem::path& state_dir,
    std::chrono::seconds timeout,
    Logger& log)
{
    std::filesystem::create_directories(state_dir);
    const std::string storage = state_dir.string();
    int rc = zts_init_from_storage(storage.c_str());
    if (rc != ZTS_ERR_OK) {
        throw std::runtime_error("zts_init_from_storage failed: " + std::to_string(rc));
    }

    g_zt_event_log = &log;
    rc = zts_init_set_event_handler(&on_zts_event);
    if (rc != ZTS_ERR_OK) {
        throw std::runtime_error("zts_init_set_event_handler failed: " + std::to_string(rc));
    }
    log.info("peer diagnostics enabled: DIRECT=P2P, RELAY=ZeroTier relay");

    rc = zts_node_start();
    if (rc != ZTS_ERR_OK) {
        throw std::runtime_error("zts_node_start failed: " + std::to_string(rc));
    }

    const auto deadline = std::chrono::steady_clock::now() + timeout;
    log.info("waiting for ZeroTier node to become online");
    while (!zts_node_is_online()) {
        if (deadline_expired(deadline)) {
            throw std::runtime_error("timeout waiting for ZeroTier node online");
        }
        zts_util_delay(100);
    }

    const std::uint64_t node_id = zts_node_get_id();
    log.info("ZeroTier node online, node_id=" + hex_u64(node_id));
    rc = zts_net_join(network_id);
    if (rc != ZTS_ERR_OK) {
        throw std::runtime_error("zts_net_join failed: " + std::to_string(rc));
    }
    log.info("joining network=" + hex_u64(network_id));

    int last_network_status = ZTS_ERR_NO_RESULT;
    auto next_progress_log = std::chrono::steady_clock::now();
    while (!zts_net_transport_is_ready(network_id)) {
        const int network_status = zts_net_get_status(network_id);
        const auto now = std::chrono::steady_clock::now();
        if (network_status != last_network_status || now >= next_progress_log) {
            log.info("waiting for network transport, " + network_wait_detail(network_id, network_status));
            last_network_status = network_status;
            next_progress_log = now + std::chrono::seconds(5);
        }

        if (network_status_is_terminal_error(network_status)) {
            throw std::runtime_error(
                "ZeroTier network join failed: " + network_status_name(network_status)
                + ", network=" + hex_u64(network_id));
        }

        if (deadline_expired(deadline)) {
            throw std::runtime_error(
                "timeout waiting for ZeroTier network transport: "
                + network_wait_detail(network_id, network_status)
                + ", network=" + hex_u64(network_id));
        }
        zts_util_delay(250);
    }

    log.info(
        "network transport ready, status=" + network_status_name(zts_net_get_status(network_id)));

    NetworkStatus status;
    status.network_id = network_id;
    status.node_id = node_id;

    while (status.ipv4.empty() && status.ipv6.empty()) {
        status.ipv4 = assigned_address(network_id, ZTS_AF_INET);
        status.ipv6 = assigned_address(network_id, ZTS_AF_INET6);
        if (!status.ipv4.empty() || !status.ipv6.empty()) {
            break;
        }
        if (deadline_expired(deadline)) {
            throw std::runtime_error("timeout waiting for ZeroTier address assignment");
        }
        zts_util_delay(100);
    }

    log.info(
        "network ready, ipv4=" + (status.ipv4.empty() ? "-" : status.ipv4)
        + ", ipv6=" + (status.ipv6.empty() ? "-" : status.ipv6));
    return status;
}

void write_status_json(
    const std::filesystem::path& path,
    const std::map<std::string, std::string>& string_values,
    const std::map<std::string, std::uint64_t>& number_values)
{
    if (path.empty()) {
        return;
    }
    if (path.has_parent_path()) {
        std::filesystem::create_directories(path.parent_path());
    }
    const std::filesystem::path temp = path.string() + ".tmp";
    std::ofstream out(temp, std::ios::out | std::ios::trunc);
    if (!out) {
        throw std::runtime_error("unable to open status file: " + temp.string());
    }
    out << "{\n";
    bool first = true;
    for (const auto& [key, value] : string_values) {
        if (!first) {
            out << ",\n";
        }
        out << "  \"" << json_escape(key) << "\": \"" << json_escape(value) << "\"";
        first = false;
    }
    for (const auto& [key, value] : number_values) {
        if (!first) {
            out << ",\n";
        }
        out << "  \"" << json_escape(key) << "\": " << value;
        first = false;
    }
    out << "\n}\n";
    out.close();

    std::error_code ec;
    std::filesystem::remove(path, ec);
    std::filesystem::rename(temp, path);
}

SOCKET native_listen(const std::string& host, std::uint16_t port)
{
    addrinfo hints {};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;
    hints.ai_flags = AI_PASSIVE;

    addrinfo* result = nullptr;
    const std::string port_string = std::to_string(port);
    const int gai =
        getaddrinfo(host.empty() ? nullptr : host.c_str(), port_string.c_str(), &hints, &result);
    if (gai != 0) {
        throw std::runtime_error("getaddrinfo(listen) failed: " + std::to_string(gai));
    }

    SOCKET listener = INVALID_SOCKET;
    for (addrinfo* current = result; current != nullptr; current = current->ai_next) {
        listener = socket(current->ai_family, current->ai_socktype, current->ai_protocol);
        if (listener == INVALID_SOCKET) {
            continue;
        }
        if (bind(listener, current->ai_addr, static_cast<int>(current->ai_addrlen)) == 0
            && listen(listener, SOMAXCONN) == 0) {
            break;
        }
        closesocket(listener);
        listener = INVALID_SOCKET;
    }
    freeaddrinfo(result);

    if (listener == INVALID_SOCKET) {
        throw std::runtime_error(
            "unable to bind/listen native socket, WSA=" + std::to_string(WSAGetLastError()));
    }
    return listener;
}

SOCKET native_connect(const std::string& host, std::uint16_t port)
{
    addrinfo hints {};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* result = nullptr;
    const std::string port_string = std::to_string(port);
    const int gai = getaddrinfo(host.c_str(), port_string.c_str(), &hints, &result);
    if (gai != 0) {
        throw std::runtime_error("getaddrinfo(connect) failed: " + std::to_string(gai));
    }

    SOCKET connected = INVALID_SOCKET;
    for (addrinfo* current = result; current != nullptr; current = current->ai_next) {
        connected = socket(current->ai_family, current->ai_socktype, current->ai_protocol);
        if (connected == INVALID_SOCKET) {
            continue;
        }
        if (connect(connected, current->ai_addr, static_cast<int>(current->ai_addrlen)) == 0) {
            break;
        }
        closesocket(connected);
        connected = INVALID_SOCKET;
    }
    freeaddrinfo(result);

    if (connected == INVALID_SOCKET) {
        throw std::runtime_error("native connect failed, WSA=" + std::to_string(WSAGetLastError()));
    }
    return connected;
}

std::map<std::string, std::string> parse_cli(int argc, char** argv)
{
    std::map<std::string, std::string> result;
    for (int index = 1; index < argc; ++index) {
        const std::string key = argv[index];
        if (!key.starts_with("--")) {
            throw std::invalid_argument("unexpected argument: " + key);
        }
        if (key == "--help" || key == "--version") {
            result[key] = "1";
            continue;
        }
        if (index + 1 >= argc) {
            throw std::invalid_argument("missing value for " + key);
        }
        result[key] = argv[++index];
    }
    return result;
}

std::string require_arg(const std::map<std::string, std::string>& args, const std::string& name)
{
    const auto it = args.find(name);
    if (it == args.end() || it->second.empty()) {
        throw std::invalid_argument("missing required argument " + name);
    }
    return it->second;
}

std::string optional_arg(
    const std::map<std::string, std::string>& args,
    const std::string& name,
    const std::string& default_value)
{
    const auto it = args.find(name);
    return it == args.end() ? default_value : it->second;
}

std::uint16_t parse_port(const std::string& value, const std::string& field_name)
{
    const unsigned long parsed = std::stoul(value);
    if (parsed == 0 || parsed > 65535) {
        throw std::invalid_argument("invalid " + field_name + ": " + value);
    }
    return static_cast<std::uint16_t>(parsed);
}

} // namespace netloop
