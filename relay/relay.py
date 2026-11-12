#!/usr/bin/env python3
"""In-memory, one-time rendezvous relay for PenNote Bridge."""

import argparse
import base64
import http.server
import ipaddress
import re
import socket
import ssl
import sys
import threading
import time


PAIR_PATH = re.compile(r"/v1/pair/([0-9a-f]{32})")
BASE64URL = re.compile(rb"[A-Za-z0-9_-]+")
BODY_LENGTH = re.compile(r"[1-9][0-9]{0,2}")
TTL_SECONDS = 300
MAX_BODY = 512
MAX_RECORDS = 2048
MAX_CONNECTIONS = 32
REQUEST_TIMEOUT = 5
REQUESTS_PER_MINUTE = 1200
WRITES_PER_ADDRESS = 30
READS_PER_ADDRESS = 120


class Pairings:
    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.lock = threading.Lock()
        self.records = {}
        self.window = None
        self.total = 0
        self.by_address = {}

    def allow(self, address, method):
        key = (address, method)
        limit = WRITES_PER_ADDRESS if method == "PUT" else READS_PER_ADDRESS
        with self.lock:
            window = int(self.clock() // 60)
            if window != self.window:
                self.window = window
                self.total = 0
                self.by_address.clear()
            used = self.by_address.get(key, 0)
            if used >= limit or self.total >= REQUESTS_PER_MINUTE:
                return False
            self.by_address[key] = used + 1
            self.total += 1
            return True

    def publish(self, lookup, blob):
        with self.lock:
            now = self.clock()
            self.expire_records(now)
            if lookup in self.records:
                return 409
            if len(self.records) >= MAX_RECORDS:
                return 503
            self.records[lookup] = (now + TTL_SECONDS, blob)
            return 201

    def consume(self, lookup):
        with self.lock:
            record = self.records.get(lookup)
            if record is None:
                return None
            if record[0] <= self.clock():
                del self.records[lookup]
                return None
            self.records[lookup] = (record[0], None)
            return record[1]

    def expire(self):
        with self.lock:
            self.expire_records(self.clock())

    def expire_records(self, now):
        for key, (expires, _) in list(self.records.items()):
            if expires <= now:
                del self.records[key]


def valid_blob(blob):
    if not (0 < len(blob) <= MAX_BODY and BASE64URL.fullmatch(blob)):
        return False
    try:
        decoded = base64.b64decode(blob + b"=" * (-len(blob) % 4), altchars=b"-_", validate=True)
    except ValueError:
        return False
    # IV and HMAC cover a ciphertext of at least one AES block.
    return (len(decoded) >= 64 and (len(decoded) - 48) % 16 == 0
            and base64.urlsafe_b64encode(decoded).rstrip(b"=") == blob)


class RelayHandler(http.server.BaseHTTPRequestHandler):
    server_version = "PenNoteBridgeRelay/1"
    sys_version = ""

    def log_message(self, format, *args):
        # A request path contains a pairing lookup key.
        pass

    def reply(self, status, body=b""):
        self.send_response(status)
        self.send_header("Content-Type", "text/plain; charset=us-ascii")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store, max-age=0")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("Connection", "close")
        if status in (429, 503):
            self.send_header("Retry-After", "60")
        self.end_headers()
        self.close_connection = True
        if body:
            self.wfile.write(body)

    def client_address_for_limit(self):
        address = self.client_address[0]
        if self.server.behind_proxy:
            forwarded = self.headers.get_all("X-Real-IP", [])
            if len(forwarded) == 1:
                try:
                    address = str(ipaddress.ip_address(forwarded[0].strip()))
                except ValueError:
                    pass
        return address

    def lookup(self):
        match = PAIR_PATH.fullmatch(self.path)
        if match is None:
            self.reply(404)
            return None
        if not self.server.pairings.allow(self.client_address_for_limit(), self.command):
            self.reply(429)
            return None
        return match.group(1)

    def do_PUT(self):
        lookup = self.lookup()
        if lookup is None:
            return
        if self.headers.get_all("Transfer-Encoding") or self.headers.get_all("Content-Encoding"):
            self.reply(415)
            return
        lengths = self.headers.get_all("Content-Length", [])
        if not lengths:
            self.reply(411)
            return
        if len(lengths) != 1 or not BODY_LENGTH.fullmatch(lengths[0]):
            self.reply(400)
            return
        size = int(lengths[0])
        if size > MAX_BODY:
            self.reply(413)
            return
        if self.headers.get_content_type() != "text/plain":
            self.reply(415)
            return
        body = self.rfile.read(size)
        if len(body) != size or not valid_blob(body):
            self.reply(400)
            return
        self.reply(self.server.pairings.publish(lookup, body))

    def do_GET(self):
        lookup = self.lookup()
        if lookup is None:
            return
        blob = self.server.pairings.consume(lookup)
        if blob is None:
            self.reply(404)
        else:
            self.reply(200, blob)


class RelayServer(http.server.ThreadingHTTPServer):
    request_queue_size = 64

    def __init__(self, address, *, behind_proxy=False, tls=None, pairings=None):
        host = ipaddress.ip_address(address[0])
        if behind_proxy and not host.is_loopback:
            raise ValueError("Proxy mode must bind to a loopback address.")
        if not behind_proxy and tls is None:
            raise ValueError("Direct listeners require a TLS certificate and key.")
        self.address_family = socket.AF_INET6 if host.version == 6 else socket.AF_INET
        self.behind_proxy = behind_proxy
        self.tls = tls
        self.pairings = pairings if pairings is not None else Pairings()
        self.slots = threading.BoundedSemaphore(MAX_CONNECTIONS)
        super().__init__(address, RelayHandler)

    def process_request(self, request, client_address):
        if not self.slots.acquire(blocking=False):
            self.shutdown_request(request)
            return
        try:
            super().process_request(request, client_address)
        except Exception:
            self.slots.release()
            raise

    def process_request_thread(self, request, client_address):
        try:
            request.settimeout(REQUEST_TIMEOUT)
            if self.tls is not None:
                try:
                    request = self.tls.wrap_socket(request, server_side=True)
                except (ssl.SSLError, OSError):
                    self.shutdown_request(request)
                    return
            super().process_request_thread(request, client_address)
        finally:
            self.slots.release()

    def handle_error(self, request, client_address):
        # Avoid logging request content, lookup paths or client identifiers.
        pass

    def service_actions(self):
        self.pairings.expire()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1", help="local IPv4 or IPv6 address")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--cert", help="PEM certificate and chain")
    parser.add_argument("--key", help="PEM private key")
    parser.add_argument("--behind-proxy", action="store_true", help="allow HTTP on loopback behind your HTTPS proxy")
    args = parser.parse_args()

    tls = None
    if args.behind_proxy:
        if args.cert or args.key:
            parser.error("Use either --behind-proxy or --cert/--key.")
    else:
        if not args.cert or not args.key:
            parser.error("Direct TLS requires --cert and --key.")
        tls = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        tls.minimum_version = ssl.TLSVersion.TLSv1_2
        try:
            tls.load_cert_chain(args.cert, args.key)
        except OSError as error:
            parser.error(str(error))

    try:
        with RelayServer((args.bind, args.port), behind_proxy=args.behind_proxy, tls=tls) as server:
            scheme = "HTTP loopback behind HTTPS proxy" if args.behind_proxy else "HTTPS"
            print("PenNote Bridge relay listening on {} port {} ({})".format(args.bind, server.server_port, scheme), flush=True)
            try:
                server.serve_forever()
            except KeyboardInterrupt:
                pass
    except (OSError, ValueError) as error:
        print(str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
