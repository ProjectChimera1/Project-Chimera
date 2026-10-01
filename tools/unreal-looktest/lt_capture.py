"""Editor captures of a look map from its CameraActors (PLAN.md §9).

For each camera: load the map, pilot the camera, game view + realtime, raise the
high-res run-up to 64 frames, let exposure and Lumen settle for 20 s, then
AutomationLibrary.take_high_res_screenshot to out/editor_<look>_<cam>.png and
check the PNG (exists, 1920x1080, > 300 KB).
"""
import os
import time

import unreal

import lt_common as C

RES = (1920, 1080)
MIN_BYTES = 300 * 1024
TASK_TIMEOUT_S = 300.0


def _shot(look, cam_label, settle_s):
    """Generator: one capture; return its record."""
    les = C.les()
    cam = C.find_actor(cam_label)
    les.pilot_level_actor(cam)
    les.editor_set_game_view(True)
    les.editor_set_viewport_realtime(True)
    world = C.editor_world()
    unreal.SystemLibrary.execute_console_command(world, 'r.HighResScreenshotDelay 64')
    unreal.SystemLibrary.execute_console_command(world, 'r.ScreenPercentage 100')
    yield from C.wait_seconds(settle_s)
    path = f'{C.OUT}/editor_{look}_{cam_label}.png'
    if os.path.exists(path):
        os.remove(path)  # a stale file must never pass the checks below
    task = unreal.AutomationLibrary.take_high_res_screenshot(RES[0], RES[1], path, camera=cam,
                                                             force_game_view=True, delay=1.0)
    if task is None:
        raise RuntimeError('take_high_res_screenshot returned no task')
    end = time.time() + TASK_TIMEOUT_S
    while not task.is_task_done():
        if time.time() > end:
            raise TimeoutError(f'{cam_label}: screenshot task not done after {TASK_TIMEOUT_S}s')
        yield
    yield from C.wait_frames(60)
    les.eject_pilot_level_actor()
    if not os.path.isfile(path):
        raise RuntimeError(f'{path} was not written')
    size = C.png_size(path)
    nbytes = os.path.getsize(path)
    if tuple(size) != RES:
        raise RuntimeError(f'{path}: {size[0]}x{size[1]}, expected {RES[0]}x{RES[1]}')
    if nbytes <= MIN_BYTES:
        raise RuntimeError(f'{path}: {nbytes} bytes <= {MIN_BYTES} (blank or black frame?)')
    return {'cam': cam_label, 'png': path, 'size': list(size), 'bytes': nbytes}


def run(look, cams=('CAM_Gameplay', 'CAM_Close'), settle_s=20.0):
    """Capture each camera of LT_<look>; return the PNG records."""
    if look not in C.LOOKS:
        raise KeyError(f'unknown look {look!r}')
    unknown = [c for c in cams if c not in C.CAMS]
    if unknown:
        raise KeyError(f'unknown cameras {unknown}')
    os.makedirs(C.OUT, exist_ok=True)
    map_path = f'{C.MAP_DIR}/LT_{look}'
    shots = []
    for cam_label in cams:
        if not C.les().load_level(map_path):
            raise RuntimeError(f'load_level({map_path}) failed')
        anim_set, anim_total = C.animate_all_in_editor()  # transient flag, lost on load (see lt_build._pose)
        if anim_set != anim_total:
            raise RuntimeError(f'{map_path}: editor animation set on {anim_set}/{anim_total} skeletal actors')
        yield from C.wait_frames(30)
        shots.append((yield from _shot(look, cam_label, settle_s)))
    return {'look': look, 'shots': shots}
