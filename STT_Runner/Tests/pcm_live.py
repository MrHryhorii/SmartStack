"""Compare HTTP file transcription with paced raw PCM on a running CPU server."""
import argparse
import json
import pathlib
import subprocess
import threading
import time
import api_contract as http
import live_websocket as ws


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--sample', type=pathlib.Path,
                        default=pathlib.Path(__file__).resolve().parents[1] / 'Assets/warmup.wav')
    args = parser.parse_args()
    raw = subprocess.check_output(['ffmpeg', '-hide_banner', '-loglevel', 'error',
                                   '-i', str(args.sample), '-ac', '1', '-ar', '44100', '-f', 'f32le', 'pipe:1'])
    _, body = http.successful('http://127.0.0.1:5050', '/v1/audio/transcriptions',
                             [('language', 'auto'), ('stream', 'false'), ('chunking_strategy', 'auto')], args.sample.read_bytes())
    expected = json.loads(body)['text']
    connection, reader = ws.connect('language=auto&translate=false&audio_format=pcm_f32le&sample_rate=44100')
    failures = []
    def upload():
        try:
            for offset in range(0, len(raw), 8192):
                part = raw[offset:offset + 8192]
                ws.send_frame(connection, 2, part)
                time.sleep(len(part) / 4 / 44100)
            ws.send_frame(connection, 1, b'stop')
        except Exception as error:
            failures.append(error)
    sender = threading.Thread(target=upload, daemon=True)
    sender.start()
    actual = None
    try:
        while True:
            opcode, payload = ws.frame(reader)
            if opcode == 8:
                ws.send_frame(connection, 8, payload)
                break
            if opcode != 1:
                continue
            event = json.loads(payload)
            assert event['type'] != 'error', event
            if event['type'] == 'transcript.text.done':
                actual = event['text']
        sender.join(timeout=10)
        assert not failures and not sender.is_alive(), failures
        assert actual == expected, (actual, expected)
        print('PASS: HTTP file and paced PCM transcripts match:', actual)
    finally:
        reader.close()
        connection.close()
    for query in ('audio_format=pcm_f32le', 'audio_format=pcm_f32le&sample_rate=0',
                  'audio_format=bad', 'sample_rate=44100'):
        assert b' 400 ' in ws.rejected_handshake(query, 'http://127.0.0.1:5050'), query
    print('PASS: invalid PCM declarations rejected')


if __name__ == '__main__':
    main()
