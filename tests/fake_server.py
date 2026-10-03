"""Small real RFC6455 test peer; no model, network service, or pip packages needed."""
import base64
import hashlib
import json
import socket
import struct
import sys


def read_exact(connection, count):
    result = bytearray()
    while len(result) < count:
        chunk = connection.recv(count - len(result))
        if not chunk:
            raise EOFError()
        result.extend(chunk)
    return bytes(result)


def receive(connection):
    first, second = read_exact(connection, 2)
    length = second & 127
    if length == 126:
        length = struct.unpack("!H", read_exact(connection, 2))[0]
    elif length == 127:
        length = struct.unpack("!Q", read_exact(connection, 8))[0]
    if length > 1024 * 1024:
        raise ValueError("Oversized test frame")
    mask = read_exact(connection, 4) if second & 128 else None
    payload = read_exact(connection, length)
    if mask:
        payload = bytes(byte ^ mask[index % 4] for index, byte in enumerate(payload))
    return first & 15, payload


def send(connection, message):
    encoded = json.dumps(message, ensure_ascii=False).encode("utf-8")
    # Continuation frames split inside UTF-8 characters, exercising native clients.
    for index, byte in enumerate(encoded):
        opcode = 1 if index == 0 else 0
        final = 128 if index == len(encoded) - 1 else 0
        connection.sendall(bytes([final | opcode, 1, byte]))


def serve():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        listener.listen(1)
        print(listener.getsockname()[1], flush=True)
        connection, _ = listener.accept()
        with connection:
            connection.settimeout(20)
            header = bytearray()
            while not header.endswith(b"\r\n\r\n"):
                header += read_exact(connection, 1)
                if len(header) > 8192:
                    raise ValueError("Oversized handshake")
            headers = dict(line.split(":", 1) for line in header.decode().split("\r\n")[1:] if ":" in line)
            key = next(value.strip() for name, value in headers.items() if name.lower() == "sec-websocket-key")
            accept = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
            connection.sendall(f"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n".encode())
            send(connection, {"type": "session.created", "partial_transcription": True, "turn_detection": "client_commit", "max_turn_seconds": 30})
            samples = 0
            while True:
                opcode, payload = receive(connection)
                if opcode == 8:
                    return
                if opcode == 9:
                    connection.sendall(bytes([0x8A, len(payload)]) + payload)
                    continue
                if opcode != 1:
                    send(connection, {"type": "error", "error": {"message": "Realtime requests must be text frames"}})
                    return
                request = json.loads(payload)
                if request["type"] == "input_audio_buffer.append":
                    samples += len(base64.b64decode(request["audio"])) // 2
                    send(connection, {"type": "conversation.item.input_audio_transcription.delta", "delta": "你" if samples <= 5120 else "好"})
                elif request["type"] == "input_audio_buffer.commit":
                    send(connection, {"type": "conversation.item.input_audio_transcription.completed", "transcript": "你好!", "audio_duration_ms": samples // 16})
                    samples = 0


if __name__ == "__main__":
    try:
        serve()
    except (EOFError, ConnectionResetError, BrokenPipeError):
        sys.exit(0)
