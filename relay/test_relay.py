import concurrent.futures
import http.client
import shutil
import ssl
import subprocess
import threading
import unittest
import uuid
from contextlib import contextmanager
from pathlib import Path

from relay import Pairings, RelayServer, TTL_SECONDS, WRITES_PER_ADDRESS


LOOKUP = "0f52cc0c123fdffc3e5da5178e44aa5e"
BLOB = (b"AAECAwQFBgcICQoLDA0ODz62qcaFhkHfarQHdFV7jXUbgzJb6C8ZyyRFzbHaU8h51dmcvXlKe4IO21QlwyBt__w"
        b"ZWOj9cdjtO8yDXPuDNaMPhKAN3mvpn5ibxL5TUPQqRkW8-lfzQhei11YeTZHZxxRUCLeTUhxeNYgQdxnTfjG90"
        b"Om0H_aUusplERjOX_EckiPkmM_x_M6iZ3S8JlF-tA")
PATH = "/v1/pair/" + LOOKUP


@contextmanager
def running(server):
    with server:
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server.server_port
        finally:
            server.shutdown()
            thread.join()


def request(port, method, path=PATH, body=None, context=None):
    if context is None:
        connection = http.client.HTTPConnection("127.0.0.1", port, timeout=4)
    else:
        connection = http.client.HTTPSConnection("localhost", port, timeout=4, context=context)
    try:
        headers = {"Content-Type": "text/plain"} if body is not None else {}
        connection.request(method, path, body=body, headers=headers)
        result = connection.getresponse()
        return result.status, result.read(), result.getheader("Cache-Control")
    finally:
        connection.close()


class RelayTests(unittest.TestCase):
    def test_expiry_and_rate_limit(self):
        now = [0]
        store = Pairings(clock=lambda: now[0])
        self.assertEqual(store.publish(LOOKUP, BLOB), 201)
        now[0] = TTL_SECONDS - 1
        self.assertEqual(store.consume(LOOKUP), BLOB)
        self.assertIsNone(store.consume(LOOKUP))
        self.assertEqual(store.publish(LOOKUP, BLOB), 409)
        now[0] = TTL_SECONDS
        self.assertEqual(store.publish(LOOKUP, BLOB), 201)
        now[0] = TTL_SECONDS * 2
        self.assertIsNone(store.consume(LOOKUP))
        for _ in range(WRITES_PER_ADDRESS):
            self.assertTrue(store.allow("192.0.2.17", "PUT"))
        self.assertFalse(store.allow("192.0.2.17", "PUT"))
        self.assertTrue(store.allow("192.0.2.17", "GET"))
        self.assertTrue(store.allow("192.0.2.18", "PUT"))
        now[0] += 60
        self.assertTrue(store.allow("192.0.2.17", "PUT"))

    def test_single_consumer_and_input_limits(self):
        with running(RelayServer(("127.0.0.1", 0), behind_proxy=True)) as port:
            self.assertEqual(request(port, "PUT", body=BLOB), (201, b"", "no-store, max-age=0"))
            self.assertEqual(request(port, "PUT", body=BLOB)[0], 409)
            self.assertEqual(request(port, "PUT", body=b"x" * 513)[0], 413)
            self.assertEqual(request(port, "PUT", body=BLOB + b"=")[0], 400)
            self.assertEqual(request(port, "GET", PATH + "?test=1")[0], 404)
            with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
                responses = list(executor.map(lambda _: request(port, "GET"), range(8)))
            self.assertEqual(sum(response == (200, BLOB, "no-store, max-age=0") for response in responses), 1)
            self.assertEqual(sum(response[0] == 404 for response in responses), 7)
            self.assertEqual(request(port, "PUT", body=BLOB)[0], 409)

    def test_refuses_public_plain_http(self):
        with self.assertRaises(ValueError):
            RelayServer(("0.0.0.0", 0), behind_proxy=True)
        with self.assertRaises(ValueError):
            RelayServer(("127.0.0.1", 0))

    @unittest.skipUnless(shutil.which("openssl"), "OpenSSL command unavailable for the temporary TLS fixture")
    def test_direct_https(self):
        directory = Path.cwd() / ("relay-tls-test-" + uuid.uuid4().hex)
        directory.mkdir()
        cert = directory / "relay.crt"
        key = directory / "relay.key"
        config = directory / "openssl.cnf"
        try:
            config.write_text("[req]\ndistinguished_name=dn\n[dn]\n", encoding="ascii")
            subprocess.run(["openssl", "req", "-config", str(config), "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                            "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost",
                            "-keyout", str(key), "-out", str(cert)], check=True, capture_output=True)
            server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
            server_context.minimum_version = ssl.TLSVersion.TLSv1_2
            server_context.load_cert_chain(cert, key)
            client_context = ssl.create_default_context(cafile=str(cert))
            with running(RelayServer(("127.0.0.1", 0), tls=server_context)) as port:
                self.assertEqual(request(port, "PUT", body=BLOB, context=client_context)[0], 201)
                self.assertEqual(request(port, "GET", context=client_context)[1], BLOB)
                self.assertEqual(request(port, "GET", context=client_context)[0], 404)
        finally:
            for file in (config, cert, key):
                if file.exists():
                    file.unlink()
            directory.rmdir()


if __name__ == "__main__":
    unittest.main()
