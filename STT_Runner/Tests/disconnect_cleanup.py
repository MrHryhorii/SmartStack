"""Verify that disconnected uploads and live clients release request slots.

Start a warmed server with MaxConcurrentRequests=1, MaxQueuedRequests>=1,
MaxConcurrentWhisper=1, and the CPU backend, then run this script.
"""

import json
import pathlib
import socket
import time
import urllib.request

from live_websocket import connect, send_frame


BASE_URL = "http://127.0.0.1:5050"
SAMPLE = pathlib.Path(__file__).resolve().parents[1] / "Assets/warmup.wav"


def queue_status():
    with urllib.request.urlopen(f"{BASE_URL}/health", timeout=5) as response:
        return json.load(response)["queue"]


def wait_for(expected, timeout=20):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = queue_status()
        if all(status[key] == value for key, value in expected.items()):
            return
        time.sleep(0.1)
    raise AssertionError(f"Expected queue {expected}, received {queue_status()}")


def open_raw_upload():
    connection = socket.create_connection(("127.0.0.1", 5050), timeout=10)
    connection.sendall(b"POST /v1/audio/transcriptions?stream=true HTTP/1.1\r\n"
                       b"Host: 127.0.0.1:5050\r\nContent-Type: audio/wav\r\n"
                       b"Transfer-Encoding: chunked\r\n\r\n")
    audio = SAMPLE.read_bytes()[:30000]
    connection.sendall(f"{len(audio):X}\r\n".encode() + audio + b"\r\n")
    return connection


def disconnect(connection):
    try:
        connection.shutdown(socket.SHUT_RDWR)
    except OSError:
        pass
    connection.close()


def main():
    wait_for({"available": 1, "waiting": 0})

    first = open_raw_upload()
    second = None
    try:
        wait_for({"available": 0})
        second = open_raw_upload()
        wait_for({"waiting": 1})
        disconnect(second)
        second = None
        wait_for({"available": 0, "waiting": 0})
    finally:
        if second is not None:
            disconnect(second)
        disconnect(first)
    wait_for({"available": 1, "waiting": 0})
    print("PASS: queued and active HTTP disconnects released their slots")

    connection, reader = connect("language=auto")
    try:
        wait_for({"available": 0})
        send_frame(connection, 2, SAMPLE.read_bytes()[:30000])
    finally:
        disconnect(connection)
        reader.close()
    wait_for({"available": 1, "waiting": 0})
    print("PASS: interrupted WebSocket released its slot")

    request = urllib.request.Request(f"{BASE_URL}/v1/audio/transcriptions?language=fr",
                                     data=SAMPLE.read_bytes(),
                                     headers={"Content-Type": "audio/wav"})
    with urllib.request.urlopen(request, timeout=90) as response:
        assert response.status == 200 and json.load(response)["text"]
    wait_for({"available": 1, "waiting": 0})
    print("PASS: Whisper accepted a new request after disconnections")


if __name__ == "__main__":
    main()
