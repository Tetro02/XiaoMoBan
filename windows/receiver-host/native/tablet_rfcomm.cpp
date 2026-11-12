#include <winsock2.h>
#include <windows.h>
#include <ws2bth.h>

#include <cstdint>
#include <new>

#pragma comment(lib, "Ws2_32.lib")

namespace {
constexpr GUID kServiceId = {
    0xa02d496e, 0xa54d, 0x4f84, {0x9a, 0x45, 0x0b, 0x5c, 0x78, 0xf5, 0x32, 0x51}
};
constexpr long kReceiveWaitMs = 250;
constexpr ULONGLONG kSendWaitMs = 1000;

int setService(SOCKADDR_BTH& address, WSAESETSERVICEOP operation) noexcept {
    CSADDR_INFO local{};
    local.LocalAddr.lpSockaddr = reinterpret_cast<SOCKADDR*>(&address);
    local.LocalAddr.iSockaddrLength = sizeof(address);
    local.iSocketType = SOCK_STREAM;
    local.iProtocol = BTHPROTO_RFCOMM;

    GUID serviceId = kServiceId;
    wchar_t serviceName[] = L"\u5C0F\u58A8\u677F";
    WSAQUERYSETW query{};
    query.dwSize = sizeof(query);
    query.lpszServiceInstanceName = serviceName;
    query.lpServiceClassId = &serviceId;
    query.dwNameSpace = NS_BTH;
    query.dwNumberOfCsAddrs = 1;
    query.lpcsaBuffer = &local;
    return WSASetServiceW(&query, operation, 0);
}

struct Listener {
    SOCKET socket = INVALID_SOCKET;
    SOCKADDR_BTH address{};
    bool registered = false;

    ~Listener() {
        if (registered) setService(address, RNRSERVICE_DELETE);
        if (socket != INVALID_SOCKET) closesocket(socket);
        WSACleanup();
    }
};

void setError(unsigned* error, int code) noexcept {
    if (error) *error = static_cast<unsigned>(code);
}

bool makeNonblocking(SOCKET socket) noexcept {
    u_long nonblocking = 1;
    return ioctlsocket(socket, FIONBIO, &nonblocking) == 0;
}

int waitForSocket(SOCKET socket, bool writing, long waitMs) noexcept {
    fd_set sockets;
    FD_ZERO(&sockets);
    FD_SET(socket, &sockets);
    timeval timeout{waitMs / 1000, (waitMs % 1000) * 1000};
    return select(0, writing ? nullptr : &sockets, writing ? &sockets : nullptr, nullptr, &timeout);
}

SOCKET peerSocket(std::uintptr_t peer) noexcept {
    return static_cast<SOCKET>(peer - 1);
}
}  // namespace

// The owning worker serializes access and closes its peer before the listener.
extern "C" void* tablet_bt_open(unsigned* error) noexcept {
    setError(error, 0);
    WSADATA data{};
    const int startup = WSAStartup(MAKEWORD(2, 2), &data);
    if (startup != 0) {
        setError(error, startup);
        return nullptr;
    }

    auto* listener = new (std::nothrow) Listener;
    if (!listener) {
        setError(error, WSAENOBUFS);
        WSACleanup();
        return nullptr;
    }

    listener->socket = socket(AF_BTH, SOCK_STREAM, BTHPROTO_RFCOMM);
    listener->address.addressFamily = AF_BTH;
    listener->address.port = BT_PORT_ANY;
    auto* address = reinterpret_cast<SOCKADDR*>(&listener->address);
    int addressLength = sizeof(listener->address);
    const ULONG require = TRUE;
    const auto* option = reinterpret_cast<const char*>(&require);

    if (listener->socket == INVALID_SOCKET ||
        setsockopt(listener->socket, SOL_RFCOMM, SO_BTH_AUTHENTICATE, option, sizeof(require)) != 0 ||
        setsockopt(listener->socket, SOL_RFCOMM, SO_BTH_ENCRYPT, option, sizeof(require)) != 0 ||
        bind(listener->socket, address, addressLength) != 0 ||
        getsockname(listener->socket, address, &addressLength) != 0 ||
        listen(listener->socket, SOMAXCONN) != 0 ||
        !makeNonblocking(listener->socket) ||
        setService(listener->address, RNRSERVICE_REGISTER) != 0) {
        setError(error, WSAGetLastError());
        delete listener;
        return nullptr;
    }

    listener->registered = true;
    return listener;
}

extern "C" std::uintptr_t tablet_bt_accept(void* handle, unsigned* error) noexcept {
    setError(error, 0);
    if (!handle) {
        setError(error, WSAEINVAL);
        return 0;
    }

    const SOCKET listener = static_cast<Listener*>(handle)->socket;
    const int ready = waitForSocket(listener, false, kReceiveWaitMs);
    if (ready <= 0) {
        if (ready == SOCKET_ERROR) setError(error, WSAGetLastError());
        return 0;
    }

    const SOCKET peer = accept(listener, nullptr, nullptr);
    if (peer == INVALID_SOCKET) {
        const int failure = WSAGetLastError();
        if (failure != WSAEWOULDBLOCK) setError(error, failure);
        return 0;
    }
    if (!makeNonblocking(peer)) {
        setError(error, WSAGetLastError());
        closesocket(peer);
        return 0;
    }

    return static_cast<std::uintptr_t>(peer) + 1;
}

extern "C" int tablet_bt_recv(std::uintptr_t peer, void* output, int length) noexcept {
    if (!peer || !output || length <= 0) return -1;

    const SOCKET socket = peerSocket(peer);
    const int ready = waitForSocket(socket, false, kReceiveWaitMs);
    if (ready == 0) return -2;
    if (ready == SOCKET_ERROR) return -1;

    const int count = recv(socket, static_cast<char*>(output), length, 0);
    if (count == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) return -2;
    return count;
}

extern "C" int tablet_bt_send(std::uintptr_t peer, const void* data, int length) noexcept {
    if (!peer || length < 0 || (!data && length != 0)) return -1;

    const SOCKET socket = peerSocket(peer);
    const auto* bytes = static_cast<const char*>(data);
    const ULONGLONG deadline = GetTickCount64() + kSendWaitMs;
    int sent = 0;
    while (sent < length) {
        const ULONGLONG now = GetTickCount64();
        if (now >= deadline || waitForSocket(socket, true, static_cast<long>(deadline - now)) <= 0) {
            return -1;
        }

        const int count = send(socket, bytes + sent, length - sent, 0);
        if (count == SOCKET_ERROR && WSAGetLastError() == WSAEWOULDBLOCK) continue;
        if (count <= 0) return -1;
        sent += count;
    }
    return sent;
}

extern "C" void tablet_bt_close_peer(std::uintptr_t peer) noexcept {
    if (peer) closesocket(peerSocket(peer));
}

extern "C" void tablet_bt_close(void* handle) noexcept {
    delete static_cast<Listener*>(handle);
}
