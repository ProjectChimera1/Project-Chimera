#!/usr/bin/env python3
"""inject_mouse.py - the real OS mouse for P10 (plan C C8). ctypes only, no third-party packages.

Launched by run_terrain.ps1 -Inject beside the game:  python inject_mouse.py --out <run dir> --pid <game pid>
  1. SetProcessDpiAwareness(2): every coordinate below is a physical pixel.
  2. Pre-checks, recorded in inject.json: LogonUI.exe absent (the screen is not locked), the input desktop can be opened, parsecd state.
     A locked screen exits 4 with the reason (the controller's -ChimeraTerrainSynthMouse fallback is then the run's only mouse, source=slate).
  3. Finds the game window of --pid, waits for <out>/mouse_targets.json (written by the director's await_mouse op), brings the window to the
     foreground, checks GetClientRect against the targets' viewport.
  4. SendInput: keys ] ] = = = = (brush d 20->30, s 10->30), then per target: move to from_px, press LMB, drag 1.83 s to to_px at ~120 Hz,
     hold 0.15 s, release (about 60 ticks: plan C C8's fixed stroke). Stroke 2 is preceded by key 2 (lower), stroke 3 by key 1 (raise) and
     gets Ctrl+Z at mid-drag (must be ignored while LMB is held). After the strokes the TAIL keys: Ctrl+Z, Ctrl+Y, [ - ] =, 3 4 5, Shift+2.
     Before every key tap and LMB press, and every 12 drag steps, the game window must hold the foreground and own the window under the
     point (guard); otherwise LMB and the modifiers are released and the injector exits 3, so nothing lands in another window.
     Every SendInput return value and the resulting GetCursorPos are logged; an injected-count of 0 (UIPI / blocked) is reported and exits 3.
Exit: 0 all strokes and tail keys sent; 3 injection blocked, window never foreground or lost mid-run; 4 screen locked; 5 window or
targets never appeared.
"""
import argparse
import ctypes
import json
import os
import subprocess
import sys
import time
from ctypes import wintypes

user32 = ctypes.WinDLL("user32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

INPUT_MOUSE, INPUT_KEYBOARD = 0, 1
MOUSEEVENTF_MOVE, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP = 0x0001, 0x0002, 0x0004
MOUSEEVENTF_ABSOLUTE, MOUSEEVENTF_VIRTUALDESK = 0x8000, 0x4000
KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE = 0x0002, 0x0008
ULONG_PTR = ctypes.c_size_t


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wintypes.LONG), ("dy", wintypes.LONG), ("mouseData", wintypes.DWORD), ("dwFlags", wintypes.DWORD),
                ("time", wintypes.DWORD), ("dwExtraInfo", ULONG_PTR)]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wintypes.WORD), ("wScan", wintypes.WORD), ("dwFlags", wintypes.DWORD), ("time", wintypes.DWORD),
                ("dwExtraInfo", ULONG_PTR)]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wintypes.DWORD), ("wParamL", wintypes.WORD), ("wParamH", wintypes.WORD)]


class _U(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _anonymous_ = ("u",)
    _fields_ = [("type", wintypes.DWORD), ("u", _U)]


user32.SendInput.argtypes = [wintypes.UINT, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = wintypes.UINT
user32.GetForegroundWindow.restype = wintypes.HWND
user32.SetForegroundWindow.argtypes = [wintypes.HWND]
user32.IsWindowVisible.argtypes = [wintypes.HWND]
user32.GetWindowThreadProcessId.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.DWORD)]
user32.GetClientRect.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.RECT)]
user32.ClientToScreen.argtypes = [wintypes.HWND, ctypes.POINTER(wintypes.POINT)]
user32.GetCursorPos.argtypes = [ctypes.POINTER(wintypes.POINT)]
user32.GetClassNameW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.GetWindowTextW.argtypes = [wintypes.HWND, wintypes.LPWSTR, ctypes.c_int]
user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
user32.BringWindowToTop.argtypes = [wintypes.HWND]
user32.OpenInputDesktop.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
user32.OpenInputDesktop.restype = wintypes.HANDLE
user32.CloseDesktop.argtypes = [wintypes.HANDLE]
user32.GetSystemMetrics.argtypes = [ctypes.c_int]
user32.MapVirtualKeyW.argtypes = [wintypes.UINT, wintypes.UINT]
user32.VkKeyScanW.argtypes = [wintypes.WCHAR]
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
user32.EnumWindows.argtypes = [WNDENUMPROC, wintypes.LPARAM]
user32.WindowFromPoint.argtypes = [wintypes.POINT]
user32.WindowFromPoint.restype = wintypes.HWND
user32.GetAncestor.argtypes = [wintypes.HWND, wintypes.UINT]
user32.GetAncestor.restype = wintypes.HWND

VK = {"]": 0xDD, "[": 0xDB, "=": 0xBB, "-": 0xBD, "1": 0x31, "2": 0x32, "3": 0x33, "4": 0x34, "5": 0x35,
      "ctrl": 0x11, "z": 0x5A, "y": 0x59, "shift": 0x10}
# After the three strokes (plan C 3.4 keys the strokes do not use): undo, redo, size and strength back and forth, modes 3-5, then
# Shift+2 (paint layer 1) so the HUD shows a paint brush. The director's await_mouse waits for these 10 applied actions (tail=10).
TAIL = [("ctrl", "z"), ("ctrl", "y"), (None, "["), (None, "-"), (None, "]"), (None, "="), (None, "3"), (None, "4"), (None, "5"), ("shift", "2")]
LOG = []
T0 = time.time()


def log(msg):
    line = "%7.2f %s" % (time.time() - T0, msg)
    LOG.append(line)
    print(line, flush=True)


def send(inputs):
    arr = (INPUT * len(inputs))(*inputs)
    n = user32.SendInput(len(inputs), arr, ctypes.sizeof(INPUT))
    if n != len(inputs):
        log("SendInput injected %d of %d (GetLastError=%d)" % (n, len(inputs), ctypes.get_last_error()))
    return n


def key_input(vk, up):
    scan = user32.MapVirtualKeyW(vk, 0)
    i = INPUT(type=INPUT_KEYBOARD)
    i.ki = KEYBDINPUT(wVk=vk, wScan=scan, dwFlags=KEYEVENTF_KEYUP if up else 0, time=0, dwExtraInfo=0)
    return i


def key_tap(name, hold=0.08):
    vk = VK[name]
    n1 = send([key_input(vk, False)])
    time.sleep(hold)
    n2 = send([key_input(vk, True)])
    time.sleep(0.12)
    return n1 + n2


def virtual_screen():
    return (user32.GetSystemMetrics(76), user32.GetSystemMetrics(77), user32.GetSystemMetrics(78), user32.GetSystemMetrics(79))


def mouse_input(flags, x=None, y=None):
    i = INPUT(type=INPUT_MOUSE)
    dx = dy = 0
    if x is not None:
        vx, vy, vw, vh = virtual_screen()
        dx = int(round((x - vx) * 65535.0 / max(1, vw - 1)))
        dy = int(round((y - vy) * 65535.0 / max(1, vh - 1)))
        flags |= MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE
    i.mi = MOUSEINPUT(dx=dx, dy=dy, mouseData=0, dwFlags=flags, time=0, dwExtraInfo=0)
    return i


def cursor():
    p = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return p.x, p.y


def move_to(x, y):
    n = send([mouse_input(0, x, y)])
    return n


def process_running(name):
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq %s" % name, "/FO", "CSV", "/NH"], capture_output=True, text=True).stdout
    return name.lower() in out.lower()


def find_window(pid):
    found = []

    def cb(hwnd, _):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value == pid and user32.IsWindowVisible(hwnd):
            r = wintypes.RECT()
            user32.GetClientRect(hwnd, ctypes.byref(r))
            cls = ctypes.create_unicode_buffer(256)
            user32.GetClassNameW(hwnd, cls, 256)
            title = ctypes.create_unicode_buffer(256)
            user32.GetWindowTextW(hwnd, title, 256)
            found.append((r.right * r.bottom, hwnd, cls.value, title.value, r.right, r.bottom))
        return True

    user32.EnumWindows(WNDENUMPROC(cb), 0)
    found.sort(reverse=True)
    return found[0] if found else None


def foreground(hwnd):
    """Bring the window to the foreground. First without any key; only if Windows refuses, an Alt tap lifts the foreground lock for this
    process (a documented trick) - that tap goes to whichever window holds the foreground, so it is the fallback, not the first try."""
    for attempt in range(6):
        if user32.GetForegroundWindow() == hwnd:
            return True
        if attempt > 0:
            send([key_input(0x12, False), key_input(0x12, True)])
        user32.ShowWindow(hwnd, 9)
        user32.BringWindowToTop(hwnd)
        user32.SetForegroundWindow(hwnd)
        time.sleep(0.4)
    return user32.GetForegroundWindow() == hwnd


class LostWindow(Exception):
    """The game window lost the foreground, or another window covers the point about to be clicked: stop injecting."""


def guard(hwnd, what, point=None):
    """Before every key tap and every LMB press (and during drags): the game window must hold the foreground and, for a point, own the
    window under it. Otherwise the input would land in someone else's window, so the injector stops (exit 3)."""
    fg = user32.GetForegroundWindow()
    if fg != hwnd:
        raise LostWindow("%s: foreground is 0x%x, not the game window 0x%x" % (what, fg or 0, hwnd))
    if point is not None:
        under = user32.WindowFromPoint(wintypes.POINT(int(point[0]), int(point[1])))
        root = user32.GetAncestor(under, 2) if under else None  # GA_ROOT
        if root != hwnd:
            raise LostWindow("%s: the window under (%d,%d) is 0x%x, not the game window" % (what, point[0], point[1], root or 0))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--pid", type=int, required=True)
    ap.add_argument("--wait-min", type=float, default=25.0, help="how long to wait for the window and the targets (shader compiles)")
    # Plan C C8: a 60-tick stroke. The press applies one tick and the 30 Hz accumulator the rest while LMB is held, so LMB is held about
    # 59.5 / 30 = 1.98 s: drag 1.83 s plus the 0.15 s end hold.
    ap.add_argument("--drag-s", type=float, default=1.83)
    ap.add_argument("--end-hold-s", type=float, default=0.15)
    a = ap.parse_args()
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(2)
    except Exception as e:  # already set, or older Windows
        log("SetProcessDpiAwareness: %s" % e)
    rec = {"pid": a.pid, "started": time.strftime("%Y-%m-%dT%H:%M:%S"), "strokes": []}
    outjson = os.path.join(a.out, "inject.json")

    def finish(code, reason):
        rec["exit"] = code
        rec["reason"] = reason
        rec["log"] = LOG
        with open(outjson, "w", encoding="utf-8") as f:
            json.dump(rec, f, indent=1)
        log("exit %d: %s" % (code, reason))
        return code

    logonui = process_running("LogonUI.exe")
    desk = user32.OpenInputDesktop(0, False, 0x0100)
    if desk:
        user32.CloseDesktop(desk)
    rec["precheck"] = {"LogonUI_running": logonui, "input_desktop_openable": bool(desk), "parsecd_running": process_running("parsecd.exe"),
                       "virtual_screen": virtual_screen()}
    log("precheck %s" % json.dumps(rec["precheck"]))
    if logonui or not desk:
        return finish(4, "screen is locked (LogonUI running=%s, input desktop openable=%s)" % (logonui, bool(desk)))

    deadline = time.time() + a.wait_min * 60
    win = None
    while time.time() < deadline and win is None:
        win = find_window(a.pid)
        if win is None:
            time.sleep(1.0)
    if win is None:
        return finish(5, "no visible window of pid %d within %.0f min" % (a.pid, a.wait_min))
    _, hwnd, cls, title, cw, ch = win
    rec["window"] = {"class": cls, "title": title, "client": [cw, ch]}
    log("window %s '%s' client %dx%d" % (cls, title, cw, ch))

    tpath = os.path.join(a.out, "mouse_targets.json")
    targets = None
    while time.time() < deadline and targets is None:
        if os.path.exists(tpath):
            try:
                with open(tpath, encoding="utf-8") as f:
                    targets = json.load(f)
            except Exception:
                time.sleep(0.3)
        else:
            time.sleep(0.5)
    if targets is None:
        return finish(5, "mouse_targets.json never appeared in %s" % a.out)
    log("targets: %d strokes, viewport %s" % (len(targets["strokes"]), targets["viewport"]))
    rec["viewport_targets"] = targets["viewport"]

    time.sleep(1.0)
    fg_ok = foreground(hwnd)
    rec["foreground"] = fg_ok
    log("foreground=%s" % fg_ok)
    if not fg_ok:
        return finish(3, "could not bring the game window to the foreground")
    r = wintypes.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(r))
    pt = wintypes.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(pt))
    ox, oy = pt.x, pt.y
    rec["client_origin"] = [ox, oy]
    rec["client_size"] = [r.right, r.bottom]
    log("client origin (%d,%d) size %dx%d" % (ox, oy, r.right, r.bottom))
    if [r.right, r.bottom] != list(targets["viewport"]):
        log("WARNING client size %s differs from the viewport %s" % ([r.right, r.bottom], targets["viewport"]))

    # park the cursor inside the window, then check the OS really moves it (UIPI / blocked injection shows here)
    cx, cy = ox + r.right // 2, oy + r.bottom // 2
    move_to(cx, cy)
    time.sleep(0.3)
    got = cursor()
    rec["cursor_probe"] = {"sent": [cx, cy], "got": list(got)}
    log("cursor probe sent (%d,%d) got (%d,%d)" % (cx, cy, got[0], got[1]))
    if abs(got[0] - cx) > 2 or abs(got[1] - cy) > 2:
        return finish(3, "the OS did not move the cursor to the injected position (blocked)")
    time.sleep(0.5)

    # Every key tap and LMB press below is guarded (foreground + the window under the point); a lost window releases LMB and exits 3.
    held = {"lmb": False}

    def tap(name, mod=None):
        guard(hwnd, "key %s%s" % (mod + "+" if mod else "", name))
        n = 0
        if mod:
            n += send([key_input(VK[mod], False)])
        n += key_tap(name)
        if mod:
            n += send([key_input(VK[mod], True)])
        return n

    try:
        # brush: d 20 -> 30 (] twice), s 10 -> 30 (= four times)
        keys_ok = 0
        for k in ["]", "]", "=", "=", "=", "="]:
            keys_ok += tap(k)
        rec["brush_keys_injected"] = keys_ok
        time.sleep(0.3)

        drag_s = a.drag_s
        for i, t in enumerate(targets["strokes"]):
            sx, sy = ox + t["from_px"][0], oy + t["from_px"][1]
            ex, ey = ox + t["to_px"][0], oy + t["to_px"][1]
            if i == 1:
                tap("2")
            if i == 2:
                tap("1")
            move_to(round(sx), round(sy))
            time.sleep(0.5)
            at_start = cursor()
            guard(hwnd, "LMB down of stroke %d" % i, (round(sx), round(sy)))
            send([mouse_input(MOUSEEVENTF_LEFTDOWN)])
            held["lmb"] = True
            t0 = time.time()
            ctrlz_done = False
            steps = 0
            while True:
                f = (time.time() - t0) / drag_s
                if f >= 1.0:
                    break
                px, py = round(sx + (ex - sx) * f), round(sy + (ey - sy) * f)
                if steps % 12 == 0:
                    guard(hwnd, "drag of stroke %d" % i, (px, py))
                move_to(px, py)
                steps += 1
                if i == 2 and f >= 0.5 and not ctrlz_done:
                    # Ctrl+Z while LMB is held: the controller must ignore it
                    guard(hwnd, "Ctrl+Z mid-drag")
                    send([key_input(VK["ctrl"], False)])
                    key_tap("z")
                    send([key_input(VK["ctrl"], True)])
                    ctrlz_done = True
                time.sleep(1.0 / 120.0)
            move_to(round(ex), round(ey))
            time.sleep(a.end_hold_s)
            at_end = cursor()
            send([mouse_input(MOUSEEVENTF_LEFTUP)])
            held["lmb"] = False
            s = {"index": i, "from_screen": [round(sx), round(sy)], "to_screen": [round(ex), round(ey)], "cursor_at_start": list(at_start),
                 "cursor_at_end": list(at_end), "move_steps": steps, "ctrl_z_mid_drag": ctrlz_done, "lmb_held_s": time.time() - t0}
            rec["strokes"].append(s)
            log("stroke %d sent: %s" % (i, json.dumps(s)))
            time.sleep(0.6)

        tail_sent = []
        for mod, k in TAIL:
            tap(k, mod)
            tail_sent.append((mod + "+" if mod else "") + k)
            time.sleep(0.25)
        rec["tail_keys"] = tail_sent
        log("tail keys sent: %s" % " ".join(tail_sent))
    except LostWindow as e:
        if held["lmb"]:
            send([mouse_input(MOUSEEVENTF_LEFTUP)])
        for vk in (VK["ctrl"], VK["shift"]):
            send([key_input(vk, True)])
        return finish(3, "stopped: %s" % e)
    return finish(0, "%d strokes and %d tail keys sent through SendInput" % (len(rec["strokes"]), len(TAIL)))


if __name__ == "__main__":
    sys.exit(main())
