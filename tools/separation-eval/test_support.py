"""Shared test helpers for deterministic local HTTP fixtures."""

from __future__ import annotations

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, HTTPServer
from threading import Thread
from typing import Iterator


@contextmanager
def running_server(handler: type[BaseHTTPRequestHandler]) -> Iterator[HTTPServer]:
    server = HTTPServer(("127.0.0.1", 0), handler)
    thread = Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server
    finally:
        server.shutdown()
        server.server_close()
        thread.join()
