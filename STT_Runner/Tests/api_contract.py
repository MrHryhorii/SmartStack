"""Exercise the audio API against a running server with its models loaded.

Run: python3 Tests/api_contract.py --base-url http://127.0.0.1:5050
The server should use CPU for portable CI; the script needs only Python's standard library.
"""

import argparse
import io
import json
import pathlib
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave


def sample_bytes(path):
    with wave.open(str(path), "rb") as source:
        assert source.getframerate() == 16000 and source.getnchannels() == 1
        samples = source.readframes(min(source.getnframes(), 3 * source.getframerate()))
        output = io.BytesIO()
        with wave.open(output, "wb") as destination:
            destination.setparams(source.getparams())
            destination.writeframes(samples)
        return output.getvalue()


def send(base, route, *, fields=None, audio=None, raw=False):
    url = base + route
    if fields is None:
        request = urllib.request.Request(url)
    elif raw:
        params = urllib.parse.urlencode(fields)
        request = urllib.request.Request(url + "?" + params, data=audio,
                                         headers={"Content-Type": "audio/wav"})
    else:
        boundary = "----stt-test-" + uuid.uuid4().hex
        body = bytearray()
        if audio is not None:
            body.extend((f"--{boundary}\r\nContent-Disposition: form-data; "
                         'name="file"; filename="sample.wav"\r\n'
                         "Content-Type: audio/wav\r\n\r\n").encode())
            body.extend(audio)
            body.extend(b"\r\n")
        for key, value in fields:
            body.extend((f"--{boundary}\r\nContent-Disposition: form-data; "
                         f'name="{key}"\r\n\r\n{value}\r\n').encode())
        body.extend(f"--{boundary}--\r\n".encode())
        request = urllib.request.Request(url, data=bytes(body),
                                         headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    try:
        with urllib.request.urlopen(request, timeout=120) as response:
            return response.status, response.headers.get_content_type(), response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.headers.get_content_type(), error.read().decode()


def successful(base, route, fields, audio, *, raw=False):
    status, content_type, body = send(base, route, fields=fields, audio=audio, raw=raw)
    assert status == 200, (route, fields, status, body)
    return content_type, body


def rejected(base, route, fields, audio, expected=400):
    status, _, body = send(base, route, fields=fields, audio=audio)
    assert status == expected, (route, fields, status, body)
    assert "error" in json.loads(body), body


def events(body):
    assert "event: error" not in body, body
    return [json.loads(line[6:]) for line in body.splitlines() if line.startswith("data: ")]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default="http://127.0.0.1:5050")
    parser.add_argument("--sample", type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parents[1] / "Assets/warmup.wav")
    args = parser.parse_args()
    base = args.base_url.rstrip("/")
    audio = sample_bytes(args.sample)
    with wave.open(io.BytesIO(audio), "rb") as source:
        expected_duration = source.getnframes() / source.getframerate()
    count = 0

    for route in ("/health", "/v1/health"):
        status, _, body = send(base, route)
        assert status == 200 and json.loads(body)["status"] == "ok"
        count += 1
    status, _, body = send(base, "/v1/models")
    models = json.loads(body)["data"]
    assert status == 200 and {m["id"] for m in models} == {"whisper-1", "gpt-4o-transcribe"}
    assert len({m["loaded_model"] for m in models}) == 1
    count += 1
    for alias in ("whisper-1", "gpt-4o-transcribe"):
        status, _, body = send(base, "/v1/models/" + alias)
        assert status == 200 and json.loads(body)["id"] == alias
        count += 1
    rejected(base, "/v1/audio/transcriptions", [("model", "whisper-1")], None)
    count += 1
    rejected(base, "/v1/audio/transcriptions", [("model", "whisper-1")], b"not audio")
    count += 1
    _, body = successful(base, "/v1/audio/transcriptions", [("model", "m" * 32768)], audio)
    assert json.loads(body)["text"]
    count += 1
    rejected(base, "/v1/audio/transcriptions", [("model", "m" * 32769)], audio)
    count += 1

    transcription = "/v1/audio/transcriptions"
    translation = "/v1/audio/translations"
    for endpoint in (transcription, translation):
        _, body = successful(base, endpoint, [], audio)
        assert json.loads(body)["text"]
        count += 1
        _, body = successful(base, endpoint,
                             [("model", ""), ("prompt", ""), ("temperature", ""),
                              ("response_format", ""), ("stream", "")], audio)
        assert json.loads(body)["text"]
        count += 1
    _, body = successful(base, transcription,
                         [("chunking_strategy", ""), ("timestamp_granularities[]", "")], audio)
    assert json.loads(body)["text"]
    count += 1
    _, body = successful(base, transcription, [], audio, raw=True)
    assert json.loads(body)["text"]
    count += 1
    rejected(base, transcription, [], b"")
    count += 1
    _, body = successful(base, transcription,
                         [("model", "unrecognized-alias"), ("language", "fr"),
                          ("prompt", "Volume instructions."), ("temperature", "0.2"),
                          ("response_format", "json"), ("stream", "false")], audio)
    assert json.loads(body)["text"]
    count += 1

    for endpoint in (transcription, translation):
        options = [("model", "gpt-4o-transcribe"), ("prompt", "Volume instructions."),
                   ("temperature", "0.1")]
        for fmt in ("json", "text", "srt", "vtt", "verbose_json"):
            content_type, body = successful(base, endpoint,
                                            options + [("response_format", fmt)], audio)
            if fmt == "json":
                assert json.loads(body)["text"]
            elif fmt == "verbose_json":
                result = json.loads(body)
                assert result["text"] and result["duration"] > 0 and result["segments"]
                assert abs(result["duration"] - expected_duration) < 1e-9
                assert all(0 <= s["start"] <= s["end"] <= result["duration"] for s in result["segments"])
                assert result["segments"][0]["start"] <= result["segments"][0]["end"]
                if endpoint == translation:
                    assert result["language"] == "english"
            elif fmt == "vtt":
                assert content_type == "text/vtt" and body.startswith("WEBVTT")
            elif fmt == "srt":
                assert body.startswith("1\n") and " --> " in body
            else:
                assert content_type == "text/plain" and body.strip()
            count += 1

    for granularities, expected in [(["segment"], "segments"), (["word"], "words"),
                                    (["segment", "word"], "words")]:
        _, body = successful(base, transcription,
                             [("response_format", "verbose_json"), ("language", "fr")]
                             + [("timestamp_granularities[]", g) for g in granularities], audio)
        result = json.loads(body)
        assert result[expected], body
        assert all(0 <= item["start"] <= item["end"] <= result["duration"] for item in result[expected])
        count += 1
    _, body = successful(base, transcription,
                         [("response_format", "verbose_json"),
                          ("timestamp_granularities", '["segment","word"]')], audio)
    assert json.loads(body)["segments"] and json.loads(body)["words"]
    count += 1

    for alias in ("whisper-1", "gpt-4o-transcribe"):
        content_type, body = successful(base, transcription,
                                        [("model", alias), ("language", "fr"),
                                         ("stream", "true")], audio)
        sequence = events(body)
        assert content_type == "text/event-stream"
        assert sequence[-1]["type"] == "transcript.text.done"
        assert "".join(event["delta"] for event in sequence[:-1]) == sequence[-1]["text"]
        count += 1

    for strategy in ("auto", json.dumps({"type": "server_vad",
                                          "prefix_padding_ms": 0,
                                          "silence_duration_ms": 64,
                                          "threshold": 0.0})):
        _, body = successful(base, transcription, [("chunking_strategy", strategy)], audio)
        assert "text" in json.loads(body)
        count += 1
    _, body = successful(base, transcription,
                         [("chunking_strategy[type]", "server_vad"),
                          ("chunking_strategy[prefix_padding_ms]", "0"),
                          ("chunking_strategy[silence_duration_ms]", "64"),
                          ("chunking_strategy[threshold]", "0.0")], audio)
    assert "text" in json.loads(body)
    count += 1
    _, body = successful(base, transcription,
                         [("chunking_strategy", json.dumps({"type": "server_vad",
                                                               "threshold": 1.0}))], audio)
    assert json.loads(body)["text"] == "", body
    count += 1

    _, body = successful(base, translation,
                         [("model", "whisper-1"), ("response_format", "verbose_json")], audio)
    assert json.loads(body)["language"] == "english"
    count += 1

    _, body = successful(base, transcription,
                         [("language", "fr"), ("prompt", "Volume instructions."),
                          ("temperature", "0.2"), ("chunking_strategy", "auto"),
                          ("response_format", "json")], audio, raw=True)
    assert json.loads(body)["text"]
    count += 1
    content_type, body = successful(base, transcription,
                                    [("language", "fr"), ("stream", "true")], audio, raw=True)
    assert content_type == "text/event-stream" and events(body)[-1]["type"] == "transcript.text.done"
    count += 1

    for fields in (
        [("temperature", "-0.1")], [("temperature", "1.1")], [("temperature", "nan")],
        [("language", "not-a-language")], [("stream", "sometimes")],
        [("response_format", "diarized_json")], [("response_format", "unsupported")],
        [("timestamp_granularities[]", "word")],
        [("response_format", "verbose_json"), ("timestamp_granularities[]", "frame")],
        [("chunking_strategy", "null")], [("chunking_strategy", "{" )],
        [("chunking_strategy", '{"type":"other"}')],
        [("chunking_strategy", '{"type":"server_vad","threshold":1.1}')],
        [("chunking_strategy", '{"type":"server_vad","prefix_padding_ms":-1}')],
        [("chunking_strategy", '{"type":"server_vad","silence_duration_ms":10}')],
        [("chunking_strategy", '{"type":"server_vad","threshold":"0.5"}')],
        [("chunking_strategy[type]", "server_vad"), ("chunking_strategy[threshold]", "invalid")],
        [("include[]", "logprobs")], [("keywords[]", "volume")],
    ):
        rejected(base, transcription, fields, audio)
        count += 1
    for fields in ([("language", "fr")], [("chunking_strategy", "auto")],
                   [("stream", "true")], [("timestamp_granularities[]", "word")]):
        rejected(base, translation, fields, audio)
        count += 1
    for fields in ([("include[]", "logprobs")], [("chunking_strategy", "null")],
                   [("stream", "sometimes")]):
        status, _, body = send(base, transcription, fields=fields, audio=audio, raw=True)
        assert status == 400 and "error" in json.loads(body), (fields, status, body)
        count += 1
    print(f"PASS: {count} HTTP contract checks")


if __name__ == "__main__":
    main()
