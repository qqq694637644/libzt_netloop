// Offline policy regression: exercises the exact inline policy called by
// Peer::sendHELLO, NodeService::nodeWirePacketSendFunction and Root ACK handler.
// No network identity, physical socket or ZeroTier Node is started.
#include "../external/libzt/ext/ZeroTierOne/node/NetloopHandoverPolicy.hpp"

#include <cstdint>
#include <iostream>
#include <map>

using namespace ZeroTier::NetloopHandoverPolicy;

#define CHECK(cond) do { \
    if (!(cond)) { \
        std::cerr << "FAILED at " << __LINE__ << ": " #cond "\n"; \
        return 1; \
    } \
} while (false)

int main()
{
#ifndef _WIN32
    std::cerr << "This regression must compile with Windows socket tagging\n";
    return 1;
#else
    // Existing socket and old/stale socket: normal Peer, ECHO and packet
    // traffic retain the PR6 Binder fallback semantics.
    const int64_t oldSocket = 0x123450;
    CHECK(udpSendMode(false, true) == SEND_SPECIFIED_SOCKET);
    CHECK(udpSendMode(false, false) == FALLBACK_TO_BINDER);

    // Strict Root mode is explicit in the dedicated wire callback, never
    // encoded into a pointer's high bits.
    CHECK(udpSendMode(true, true) == SEND_SPECIFIED_SOCKET);
    CHECK(udpSendMode(true, false) == REJECT_STALE_ROOT_SOCKET);
    CHECK(rootEndpointIndex(1, 2) == 0);
    CHECK(rootEndpointIndex(2, 2) == 1);
    CHECK(rootEndpointIndex(3, 2) == 0);

    // IP Helper errors, Tentative DAD and Preferred on the wrong interface
    // defer without consuming another attempt.
    const int64_t start = 10000;
    CHECK(rootRetryMode(0, start, start, false, false) == ROOT_RETRY_DEFER);
    CHECK(rootRetryMode(0, start, start + 250, true, false) == ROOT_RETRY_DEFER);
    CHECK(rootRetryMode(0, start, start + 500, true, true) == ROOT_RETRY_SEND);
    CHECK(rootRetryMode(1, start, start + 750, false, false) == ROOT_RETRY_DEFER);
    CHECK(rootRetryMode(1, start, start + 1000, true, true) == ROOT_RETRY_SEND);
    CHECK(rootRetryMode(3, start, start + 1500, true, true) == ROOT_RETRY_STOP);
    CHECK(rootRetryMode(1, start, start + 30000, true, true) == ROOT_RETRY_STOP);

    // Packet IDs must be matched after authenticated OK(HELLO), even if the
    // first ACK arrives after a second retry. A different Root is independent.
    std::map<uint64_t, RootHelloAttemptState> roots;
    RootHelloAttemptState &rootA = roots[1];
    RootHelloAttemptState &rootB = roots[2];
    const unsigned char ipv6a[16] = { 0x20, 0x01, 0x0d, 0xb8, 0x01 };
    const unsigned char ipv6b[16] = { 0x20, 0x01, 0x0d, 0xb8, 0x02 };
    RootBindingIdentity binding;
    binding.set(5, 100, ipv6a);
    CHECK(binding.matches(5, 100, ipv6a));
    CHECK(!binding.matches(6, 100, ipv6a));
    CHECK(!binding.matches(5, 200, ipv6a));
    CHECK(!binding.matches(5, 100, ipv6b));

    CHECK(rootA.beginAttempt(1) == 1);
    rootA.registerPacket(0xAA, oldSocket);
    CHECK(!rootA.acknowledge(0xBB, oldSocket));
    CHECK(!rootA.acknowledge(0xAA, oldSocket + 1));
    CHECK(rootA.pending());
    CHECK(rootA.beginAttempt(1) == 2);
    rootA.registerPacket(0xCC, oldSocket);
    CHECK(rootB.beginAttempt(1) == 1);
    rootB.registerPacket(0xDD, oldSocket + 2);
    CHECK(!rootB.acknowledge(0xAA, oldSocket));
    CHECK(rootA.acknowledge(0xAA, oldSocket));
    CHECK(!rootA.pending());
    CHECK(rootA.beginAttempt(1) == 0);
    CHECK(!rootA.acknowledge(0xCC, oldSocket));
    CHECK(rootB.pending());
    CHECK(rootB.acknowledge(0xDD, oldSocket + 2));
    CHECK(!rootB.pending());

    RootHelloAttemptState exhausted;
    CHECK(exhausted.beginAttempt(1) == 1);
    exhausted.registerPacket(0x10, oldSocket);
    CHECK(exhausted.beginAttempt(1) == 2);
    CHECK(exhausted.beginAttempt(1) == 3);
    CHECK(exhausted.beginAttempt(1) == 0);
    CHECK(exhausted.beginAttempt(2) == 1);
    CHECK(!exhausted.acknowledge(0x10, oldSocket));
    exhausted.registerPacket(0x20, oldSocket + 1);
    CHECK(!exhausted.acknowledge(0x20, oldSocket));
    CHECK(exhausted.beginAttempt(2) == 2);
    CHECK(exhausted.beginAttempt(2) == 3);
    CHECK(exhausted.beginAttempt(3) == 0);
    CHECK(!exhausted.acknowledge(0, oldSocket));
    CHECK(!exhausted.pending());

    std::cout << "PASS: normal UDP fallback, explicit Root mode, IPv6 target, DAD, ACK socket and endpoint rotation\n";
    return 0;
#endif
}
