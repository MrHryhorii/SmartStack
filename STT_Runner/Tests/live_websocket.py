"""Exercise the browser's full-duplex route using only Python's standard library.

Run against a warmed CPU server: python3 Tests/live_websocket.py
"""

import base64
import io
import json
import os
import pathlib
import socket
import subprocess
import tempfile
import threading
import urllib.error
import urllib.request
import wave


HOST = "127.0.0.1"
PORT = 5050


def audio_bytes():
    path = pathlib.Path(__file__).resolve().parents[1] / "Assets/warmup.wav"
    with wave.open(str(path), "rb") as source:
        speech = source.readframes(source.getnframes())
        silence = b"\0" * (source.getframerate() * source.getsampwidth() * 2)
        output = io.BytesIO()
        with wave.open(output, "wb") as destination:
            destination.setparams(source.getparams())
            for _ in range(2):
                destination.writeframes(speech)
                destination.writeframes(silence)
        return output.getvalue(), 44 + len(speech) + len(silence)


def read_exact(reader, size):
    value = reader.read(size)
    if len(value) != size:
        raise EOFError("WebSocket closed unexpectedly")
    return value


def frame(reader):
    first, second = read_exact(reader, 2)
    opcode = first & 15
    length = second & 127
    if length == 126:
        length = int.from_bytes(read_exact(reader, 2), "big")
    elif length == 127:
        length = int.from_bytes(read_exact(reader, 8), "big")
    assert not second & 128
    return opcode, read_exact(reader, length)


def send_frame(connection, opcode, data):
    mask = os.urandom(4)
    if len(data) < 126:
        length = bytes([len(data) | 128])
    elif len(data) < 65536:
        length = bytes([254]) + len(data).to_bytes(2, "big")
    else:
        length = bytes([255]) + len(data).to_bytes(8, "big")
    masked = bytes(value ^ mask[index % 4] for index, value in enumerate(data))
    connection.sendall(bytes([128 | opcode]) + length + mask + masked)


def connect(query, origin=None):
    connection = socket.create_connection((HOST, PORT), timeout=90)
    reader = connection.makefile("rb")
    key = base64.b64encode(os.urandom(16)).decode()
    if origin is None:
        origin = f"http://{HOST}:{PORT}"
    connection.sendall((f"GET /live?{query} HTTP/1.1\r\nHost: {HOST}:{PORT}\r\n"
                        f"Origin: {origin}\r\nUpgrade: websocket\r\n"
                        f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
                        "Sec-WebSocket-Version: 13\r\n\r\n").encode())
    assert reader.readline().startswith(b"HTTP/1.1 101"), "WebSocket upgrade failed"
    while reader.readline() != b"\r\n":
        pass
    assert json.loads(frame(reader)[1])["type"] == "session.ready"
    return connection, reader


def rejected_handshake(query, origin):
    with socket.create_connection((HOST, PORT), timeout=15) as connection:
        key = base64.b64encode(os.urandom(16)).decode()
        connection.sendall((f"GET /live?{query} HTTP/1.1\r\nHost: {HOST}:{PORT}\r\n"
                            f"Origin: {origin}\r\nUpgrade: websocket\r\n"
                            f"Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\n"
                            "Sec-WebSocket-Version: 13\r\n\r\n").encode())
        return connection.recv(128).split(b"\r\n", 1)[0]


def check_session(translate):
    audio, boundary = audio_bytes()
    connection, reader = connect(f"language=fr&translate={str(translate).lower()}")
    release = threading.Event()
    uploaded = threading.Event()
    failures = []

    def upload():
        try:
            for offset in range(0, boundary, 16384):
                send_frame(connection, 2, audio[offset:min(offset + 16384, boundary)])
            release.wait(35)
            for offset in range(boundary, len(audio), 16384):
                send_frame(connection, 2, audio[offset:offset + 16384])
            send_frame(connection, 1, b"stop")
            uploaded.set()
        except Exception as error:
            failures.append(error)
            uploaded.set()

    sender = threading.Thread(target=upload, daemon=True)
    sender.start()
    deltas = []
    first_before_eof = False
    done = None
    try:
        while True:
            opcode, data = frame(reader)
            if opcode == 8:
                send_frame(connection, 8, data)
                break
            if opcode != 1:
                continue
            event = json.loads(data)
            assert event["type"] != "error", event
            if event["type"] == "transcript.text.delta":
                if not deltas:
                    first_before_eof = not uploaded.is_set()
                    release.set()
                assert event["start"] >= 0 and event["end"] >= event["start"]
                assert event["index"] == len(deltas)
                deltas.append(event["delta"])
            if event["type"] == "transcript.text.done":
                done = event
        sender.join(timeout=5)
        assert not failures and not sender.is_alive(), failures
        assert first_before_eof and len(deltas) >= 2
        assert "".join(deltas) == done["text"]
        if translate:
            assert done["language"] == "english"
        else:
            assert done["language"] == "french"
        print(f"PASS: {'translation' if translate else 'transcription'}, {len(deltas)} timed deltas before EOF")
    finally:
        release.set()
        reader.close()
        connection.close()


def check_encoded_audio():
    source = pathlib.Path(__file__).resolve().parents[1] / "Assets/warmup.wav"
    with tempfile.TemporaryDirectory() as folder:
        for extension, encoder in (("webm", "libopus"), ("ogg", "libopus")):
            target = pathlib.Path(folder) / ("sample." + extension)
            subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y",
                            "-i", str(source), "-c:a", encoder, str(target)], check=True)
            connection, reader = connect("language=fr&translate=false")
            try:
                data = target.read_bytes()
                for offset in range(0, len(data), 16384):
                    send_frame(connection, 2, data[offset:offset + 16384])
                send_frame(connection, 1, b"stop")
                final = None
                while True:
                    opcode, payload = frame(reader)
                    if opcode == 8:
                        send_frame(connection, 8, payload)
                        break
                    if opcode != 1:
                        continue
                    event = json.loads(payload)
                    assert event["type"] != "error", event
                    if event["type"] == "transcript.text.done":
                        final = event["text"]
                assert final, extension
                print(f"PASS: {extension} microphone stream")
            finally:
                reader.close()
                connection.close()


def main():
    with urllib.request.urlopen(f"http://{HOST}:{PORT}/v1/languages") as response:
        languages = json.load(response)["data"]
        assert languages[0]["code"] == "auto" and any(item["code"] == "fr" for item in languages)
    with urllib.request.urlopen(f"http://{HOST}:{PORT}/") as response:
        assert b"Mwandishi STT" in response.read()
    try:
        urllib.request.urlopen(f"http://{HOST}:{PORT}/live")
        raise AssertionError("Expected HTTP 426 without WebSocket upgrade")
    except urllib.error.HTTPError as error:
        assert error.code == 426
    assert b" 400 " in rejected_handshake("language=not-a-language", f"http://{HOST}:{PORT}")
    connection, reader = connect("language=auto", "https://other.example")
    reader.close()
    connection.close()
    check_session(False)
    check_session(True)
    check_encoded_audio()


if __name__ == "__main__":
    main()
