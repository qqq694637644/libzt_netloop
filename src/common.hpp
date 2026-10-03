#pragma once

#include <WinSock2.h>

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <string>

namespace netloop {

class Logger {
public:
    Logger(std::string role, const std::filesystem::path& log_path);
    void info(const std::string& message);
    void error(const std::string& message);

private:
    void write(const char* level, const std::string& message);

    std::string role_;
    std::ofstream file_;
    std::mutex mutex_;
};

class WinsockRuntime {
public:
    WinsockRuntime();
    ~WinsockRuntime();
    WinsockRuntime(const WinsockRuntime&) = delete;
    WinsockRuntime& operator=(const WinsockRuntime&) = delete;
};

struct NetworkStatus {
    std::uint64_t network_id = 0;
    std::uint64_t node_id = 0;
    std::string ipv4;
    std::string ipv6;
};

std::uint64_t parse_hex_u64(const std::string& value);
std::string hex_u64(std::uint64_t value);

NetworkStatus start_libzt_network(
    std::uint64_t network_id,
    const std::filesystem::path& state_dir,
    std::chrono::seconds timeout,
    Logger& log);

void write_status_json(
    const std::filesystem::path& path,
    const std::map<std::string, std::string>& string_values,
    const std::map<std::string, std::uint64_t>& number_values = {});

SOCKET native_listen(const std::string& host, std::uint16_t port);
SOCKET native_connect(const std::string& host, std::uint16_t port);
void relay_native_and_zt(SOCKET native_socket, int zt_socket, Logger& log, std::uint64_t connection_id);

std::map<std::string, std::string> parse_cli(int argc, char** argv);
std::string require_arg(const std::map<std::string, std::string>& args, const std::string& name);
std::string optional_arg(
    const std::map<std::string, std::string>& args,
    const std::string& name,
    const std::string& default_value);
std::uint16_t parse_port(const std::string& value, const std::string& field_name);

} // namespace netloop
