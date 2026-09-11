"""Integration checks against an isolated published server; never uses production data.

Run scripts/verify.ps1 first. Add --browser to run test-room-ui.cjs with Playwright.
"""
import argparse
import io
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time
import urllib.error
import urllib.request
import uuid
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ROOM_A = "AAAA-BBBB-CCCC-DDDD-EEEE"
ROOM_B = "BBBB-CCCC-DDDD-EEEE-FFFF"
ROOM_UI = "CCCC-DDDD-EEEE-FFFF-GGGG"
UNREGISTERED = "DDDD-EEEE-FFFF-GGGG-HHHH"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--browser", action="store_true")
    parser.add_argument("--dotnet", default=str(Path(os.environ["LOCALAPPDATA"]) / "Microsoft/dotnet/dotnet.exe"))
    args = parser.parse_args()
    published = ROOT / "artifacts/render-publish"
    assert (published / "ZenConnect.dll").is_file(), "Run scripts/verify.ps1 first."
    test_dir = ROOT / "artifacts" / ("room-actions-" + uuid.uuid4().hex)
    test_dir.mkdir(parents=True)
    data_dir = test_dir / "data"
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    base = f"http://127.0.0.1:{port}"
    environment = {**os.environ, "PORT": str(port), "SCREEN_REVIEW_DATA_DIR": str(data_dir)}
    log = (test_dir / "server.log").open("w", encoding="utf-8")
    process = None

    def request(method, route, key=None, body=None, content_type=None, expected=200):
        headers = {}
        if key:
            headers["X-Screen-Review-Key"] = key
        if isinstance(body, dict):
            body = json.dumps(body).encode()
            content_type = "application/json"
        if content_type:
            headers["Content-Type"] = content_type
        req = urllib.request.Request(base + route, data=body, headers=headers, method=method)
        try:
            response = urllib.request.urlopen(req, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            payload = response.read()
            assert response.status == expected, (route, response.status, payload[:250])
            return payload, response.headers

    def api(method, route, key=None, body=None, expected=200):
        payload, _ = request(method, route, key, body, expected=expected)
        return json.loads(payload) if payload else None

    image = (ROOT / "src/ZenConnect/wwwroot/assets/room-preview.png").read_bytes()

    def upload(key):
        boundary = "test" + uuid.uuid4().hex
        body = (f'--{boundary}\r\nContent-Disposition: form-data; name="image"; filename="test.png"\r\n'
                'Content-Type: image/png\r\n\r\n').encode() + image + f"\r\n--{boundary}--\r\n".encode()
        payload, _ = request("POST", "/api/screenshots", key, body, "multipart/form-data; boundary=" + boundary)
        return json.loads(payload)

    def check_zip(payload, expected_count):
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            names = archive.namelist()
            assert len(names) == expected_count and expected_count > 0
            assert len({name.split('/')[0] for name in names}) == 1
            assert names == sorted(names)
            assert all(name.endswith(".png") and ".." not in name for name in names)
            assert all(key not in str(names) for key in (ROOM_A, ROOM_B, ROOM_UI))
            assert all(archive.read(name) == image for name in names)

    def start():
        proc = subprocess.Popen([args.dotnet, "ZenConnect.dll", "--server"], cwd=published,
                                env=environment, stdout=log, stderr=log,
                                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
        for _ in range(100):
            try:
                api("GET", "/api/health")
                return proc
            except urllib.error.URLError:
                if proc.poll() is not None:
                    raise RuntimeError("Server failed; inspect " + str(test_dir / "server.log"))
                time.sleep(.1)
        proc.terminate()
        proc.wait(timeout=10)
        raise RuntimeError("Server startup timed out")

    try:
        process = start()
        for route, method in (("/api/screenshots", "DELETE"), ("/api/screenshots/export", "GET")):
            for key in (None, "invalid", UNREGISTERED):
                api(method, route, key, expected=401)
        for key in (ROOM_A, ROOM_B, ROOM_UI):
            api("POST", "/api/rooms/register", key)
        api("GET", "/api/screenshots/export", ROOM_A, expected=400)
        a1, a2, b1 = upload(ROOM_A), upload(ROOM_A), upload(ROOM_B)
        upload(ROOM_UI)
        upload(ROOM_UI)
        message = api("POST", f'/api/screenshots/{a1["id"]}/message', ROOM_A, {"text": "Đáp án: A, C\nGiải thích"})
        assert message["text"] == "Đáp án: A, C\nGiải thích"
        api("POST", f'/api/screenshots/{b1["id"]}/message', ROOM_B, {"text": "Keep this room"})
        api("DELETE", f'/api/screenshots/{b1["id"]}', ROOM_A, expected=401)
        payload, headers = request("GET", "/api/screenshots/export", ROOM_A)
        assert headers["X-Screenshot-Count"] == "2"
        check_zip(payload, 2)
        # Simulate broken files only inside the newly created test data directory.
        stored = json.loads((data_dir / "store.json").read_text(encoding="utf-8"))
        record = next(item for item in stored["Screenshots"] if item["Id"] == a2["id"])
        image_path = (data_dir / "images" / record["FileName"]).resolve()
        assert image_path.parent == (data_dir / "images").resolve()
        saved_image = image_path.read_bytes()
        image_path.unlink()
        api("GET", "/api/screenshots/export", ROOM_A, expected=409)
        image_path.write_bytes(b"")
        api("GET", "/api/screenshots/export", ROOM_A, expected=409)
        image_path.write_bytes(saved_image)
        result = api("DELETE", "/api/screenshots", ROOM_A)
        assert result == {"deletedScreenshotCount": 2, "deletedMessageCount": 1, "failedImageCount": 0}
        assert api("GET", "/api/screenshots", ROOM_A) == []
        assert api("GET", "/api/messages", ROOM_A) == []
        assert not image_path.exists()
        assert len(api("GET", "/api/screenshots", ROOM_B)) == 1
        assert len(api("GET", "/api/messages", ROOM_B)) == 1
        assert api("GET", "/api/room", ROOM_A)["session"] == ROOM_A
        assert api("DELETE", "/api/screenshots", ROOM_A)["deletedScreenshotCount"] == 0
        api("GET", "/api/screenshots/export", ROOM_A, expected=400)
        upload(ROOM_A)  # Registration survives the bulk deletion.
        print("PASS: authorization, ordered ZIP bytes, missing/empty images, room isolation, bulk delete, repeat delete and upload after delete", flush=True)
        if args.browser:
            browser_env = {**os.environ, "TEST_BASE_URL": base, "TEST_ROOM_KEY": ROOM_UI, "TEST_OUTPUT_DIR": str(test_dir)}
            subprocess.run([shutil.which("node") or "node", str(ROOT / "scripts/test-room-ui.cjs")],
                           cwd=ROOT, env=browser_env, check=True, timeout=150)
            check_zip((test_dir / "download.zip").read_bytes(), 2)
        process.terminate()
        process.wait(timeout=10)
        process = start()
        assert len(api("GET", "/api/screenshots", ROOM_B)) == 1
        assert len(api("GET", "/api/screenshots", ROOM_A)) == 1
        if args.browser:
            assert api("GET", "/api/screenshots", ROOM_UI) == []
            room_answers = api("GET", "/api/messages", ROOM_UI)
            assert len(room_answers) == 3
            assert all(item["screenshotId"] is None for item in room_answers)
        print("PASS: saved data survives restart; browser artifacts were written under artifacts/room-actions-*", flush=True)
    finally:
        if process is not None and process.poll() is None:
            process.terminate()
            process.wait(timeout=10)
        log.close()


if __name__ == "__main__":
    main()
