import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import structlog
from confluent_kafka.admin import AdminClient

log = structlog.get_logger()


def kafka_is_reachable(bootstrap_servers: str, timeout: float = 5.0) -> bool:
    try:
        AdminClient({"bootstrap.servers": bootstrap_servers}).list_topics(timeout=timeout)
        return True
    except Exception:
        return False


class _HealthHandler(BaseHTTPRequestHandler):
    bootstrap_servers = "localhost:9092"

    def do_GET(self) -> None:
        if self.path == "/health":
            self._respond(200, b'{"status":"ok"}')
        elif self.path == "/ready":
            if kafka_is_reachable(self.bootstrap_servers):
                self._respond(200, b'{"status":"ok"}')
            else:
                self._respond(503, b'{"status":"kafka unreachable"}')
        else:
            self._respond(404, b"")

    def _respond(self, status: int, body: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)

    # BaseHTTPRequestHandler logs every request to stderr by default; structlog covers this instead
    def log_message(self, format: str, *args: object) -> None:
        pass


def start_health_server(bootstrap_servers: str, port: int) -> ThreadingHTTPServer:
    handler = type("_BoundHealthHandler", (_HealthHandler,), {"bootstrap_servers": bootstrap_servers})
    server = ThreadingHTTPServer(("0.0.0.0", port), handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    log.info("health_server_started", port=port)
    return server
