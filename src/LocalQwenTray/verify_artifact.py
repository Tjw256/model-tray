"""Read-only native-app verification; never starts/stops the native model.
Run: python verify_artifact.py
Creates and terminates only its own tray process. Other tray must be closed first.
"""
import ctypes
from ctypes import wintypes
import json
import os
import re
from pathlib import Path
import struct
import subprocess
import time

root = Path(__file__).resolve().parent
exe = root / "publish" / "LocalQwenTray.exe"
data = exe.read_bytes()
pe = struct.unpack_from("<I", data, 0x3C)[0]
subsystem = struct.unpack_from("<H", data, pe + 24 + 68)[0]
assert subsystem == 2, f"Expected WINDOWS_GUI subsystem, got {subsystem}"
print("PASS published EXE is Windows GUI subsystem (no automatic console)")


def state():
    ledger = Path(os.environ["LOCALAPPDATA"]) / "LocalQwen/native-process.json"
    return {"ledger": ledger.read_text() if ledger.exists() else None}


before = state()
first = subprocess.Popen([str(exe)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
try:
    time.sleep(2)
    assert first.poll() is None, "Tray did not remain running (another tray may already be active)"
    second = subprocess.run([str(exe)], capture_output=True, timeout=10)
    assert second.returncode == 0 and first.poll() is None, "Duplicate instance did not exit harmlessly"
    print("PASS actual duplicate EXE exits while original remains running")
    user32 = ctypes.WinDLL("user32", use_last_error=True)
    enum_proc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
    user32.IsWindowVisible.argtypes = [wintypes.HWND]
    user32.EnumWindows.argtypes = [enum_proc, wintypes.LPARAM]
    visible = []

    @enum_proc
    def visit(hwnd, unused):
        pid = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value == first.pid and user32.IsWindowVisible(hwnd):
            visible.append(int(hwnd))
        return True

    user32.EnumWindows(visit, 0)
    assert not visible, f"Tray created visible top-level windows: {visible}"
    print("PASS actual default startup has zero visible application windows")
    after = state()
    assert before == after, "Qwen state changed during read-only verification (check concurrent controller activity)"
    print("PASS default tray launch leaves actual native Qwen ownership state unchanged")
    print("Observed native state:", json.dumps(after))
    config = Path(os.environ["LOCALAPPDATA"]) / "LocalQwen" / "config.json"
    assert config.exists(), "Expected config.json after the first tray run"
    key = json.loads(config.read_text(encoding="utf-8-sig"))["ApiKey"]  # utf-8-sig tolerates a BOM (e.g. saved from Notepad)
    assert key, "config.json has no ApiKey"
    log = Path(os.environ["LOCALAPPDATA"]) / "LocalQwen" / "tray.log"
    assert log.exists(), "Expected private per-user runtime log"
    for target in (log, Path(str(log) + ".previous")):
        if target.exists():
            assert key not in target.read_text(encoding="utf-8"), "API credential found in runtime log"
    print("PASS private per-user runtime logs contain no configured API credential")
finally:
    if first.poll() is None:
        first.terminate()
        first.wait(timeout=10)
