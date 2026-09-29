"""Verify that raw upload emits SSE before the client finishes sending audio.

Run against a warmed server: python3 Tests/long_stream.py
"""

import http.client
import io
import json
import pathlib
import threading
import wave


def recording(path):
    with wave.open(str(path), "rb") as source:
        assert source.getframerate() == 16000 and source.getnchannels() == 1
        speech = source.readframes(source.getnframes())
        silence = b"\0" * (source.getsampwidth() * source.getframerate() * 2)
        output = io.BytesIO()
        with wave.open(output, "wb") as destination:
            destination.setparams(source.getparams())
            for _ in range(4):
                destination.writeframes(speech)
                destination.writeframes(silence)
        first_bytes = 44 + len(speech) + len(silence)
        return output.getvalue(), first_bytes


def main():
    sample = pathlib.Path(__file__).resolve().parents[1] / "Assets/warmup.wav"
    audio, first_bytes = recording(sample)
    connection = http.client.HTTPConnection("127.0.0.1", 5050, timeout=90)
    release = threading.Event()
    finished = threading.Event()
    failures = []

    connection.putrequest("POST", "/v1/audio/transcriptions?stream=true")
    connection.putheader("Content-Type", "audio/wav")
    connection.putheader("Transfer-Encoding", "chunked")
    connection.endheaders()

    def send_audio():
        try:
            for part in (audio[:first_bytes], audio[first_bytes:]):
                connection.send(f"{len(part):X}\r\n".encode() + part + b"\r\n")
                release.wait(40)
            connection.send(b"0\r\n\r\n")
            finished.set()
        except Exception as error:
            failures.append(error)
            finished.set()

    sender = threading.Thread(target=send_audio, daemon=True)
    sender.start()
    try:
        response = connection.getresponse()
        assert response.status == 200 and response.getheader("Content-Type").startswith("text/event-stream")
        deltas = []
        done = None
        first_before_upload = False
        while line := response.readline():
            if not line.startswith(b"data: "):
                continue
            event = json.loads(line[6:])
            assert event["type"] != "error", event
            if event["type"] == "transcript.text.delta":
                if not deltas:
                    first_before_upload = not finished.is_set()
                    release.set()
                deltas.append(event["delta"])
            elif event["type"] == "transcript.text.done":
                done = event["text"]
        sender.join(timeout=5)
        assert not failures and not sender.is_alive(), failures
        assert first_before_upload, "The first SSE text arrived only after upload completion."
        assert len(deltas) >= 2 and "".join(deltas) == done, (len(deltas), done)
        print(f"PASS: {len(audio) / 1024:.0f} KiB upload, {len(deltas)} deltas, first delta before upload EOF")
    finally:
        release.set()
        connection.close()


if __name__ == "__main__":
    main()
