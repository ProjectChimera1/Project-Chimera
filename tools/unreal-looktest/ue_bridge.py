"""Editor-side job bridge for the look test.

Started by `UnrealEditor.exe <uproject> -ExecutePythonScript=<this file>`.
Keeps the Python executer alive, then on every Slate post-tick runs job files
dropped into LookTest/queue/ by the host-side `ue_job.py`.

Job file  : queue/<stem>.job.json   {"module": str, "func": str, "args": {...}}
While run : queue/<stem>.running    (the job file, renamed)
Result    : queue/<stem>.result.json {"ok", "value", "stdout", "secs"} or {"ok": false, "traceback"}

A job function may return a value, or be a generator: the bridge advances it
one step per editor tick, so the editor (and the MCP server) keep ticking
between items. The generator's `return` value becomes the result value.
"""
import contextlib
import glob
import importlib
import io
import json
import os
import sys
import time
import traceback

import unreal

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
QUEUE = LT + '/queue'
RUN = LT + '/run'
if LT + '/tools' not in sys.path:
    sys.path.insert(0, LT + '/tools')

# Without this the executer requests QUIT_EDITOR on the next tick (EditorPythonExecuter.cpp:73).
unreal.EditorPythonScripting.set_keep_python_script_alive(True)

# Keep ticking at full rate while the editor is not the foreground window (session-only, not saved).
# EditorPerformanceSettings is not exposed to Python, so go through its CDO by path and use the C++ property name
# (verified live 2026-10-01: the snake_case name fails, 'bThrottleCPUWhenNotForeground' works).
try:
    _perf = unreal.find_object(None, '/Script/UnrealEd.Default__EditorPerformanceSettings')
    _perf.set_editor_property('bThrottleCPUWhenNotForeground', False)
    _THROTTLE_OFF = not _perf.get_editor_property('bThrottleCPUWhenNotForeground')
except Exception as _e:
    _THROTTLE_OFF = repr(_e)

S = {'gen': None, 'stem': None, 'buf': None, 't0': 0.0, 'last_poll': 0.0, 'last_beat': 0.0}


def _atomic_write(path, obj):
    """Write JSON to a temp file, then rename it over the target."""
    tmp = path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump(obj, f, indent=1, default=repr)
    os.replace(tmp, path)


def _finish(result):
    """Write the result file for the current job and clear the slot."""
    stem = S['stem']
    result['stdout'] = S['buf'].getvalue()[-20000:] if S['buf'] else ''
    result['secs'] = round(time.time() - S['t0'], 2)
    _atomic_write(f'{QUEUE}/{stem}.result.json', result)
    try:
        os.remove(f'{QUEUE}/{stem}.running')
    except OSError:
        pass
    unreal.log(f'[lt_bridge] job {stem} done ok={result.get("ok")} in {result["secs"]}s')
    S.update(gen=None, stem=None, buf=None)


def _claim():
    """Take the oldest queued job, start it, and keep its generator if it is one."""
    jobs = sorted(glob.glob(QUEUE + '/*.job.json'))
    if not jobs:
        return
    path = jobs[0]
    stem = os.path.basename(path)[:-len('.job.json')]
    running = f'{QUEUE}/{stem}.running'
    os.replace(path, running)
    S.update(stem=stem, buf=io.StringIO(), t0=time.time())
    unreal.log(f'[lt_bridge] job {stem} start')
    try:
        with open(running, encoding='utf-8') as f:
            job = json.load(f)
        mod = importlib.import_module(job['module'])
        mod = importlib.reload(mod)
        # Reload the shared helpers too, so edits to lt_common land without an editor restart.
        if 'lt_common' in sys.modules and job['module'] != 'lt_common':
            importlib.reload(sys.modules['lt_common'])
            mod = importlib.reload(mod)
        fn = getattr(mod, job.get('func', 'run'))
        with contextlib.redirect_stdout(S['buf']):
            out = fn(**job.get('args', {}))
        if hasattr(out, '__next__'):
            S['gen'] = out
        else:
            _finish({'ok': True, 'value': out})
    except Exception:
        _finish({'ok': False, 'traceback': traceback.format_exc()})


def _step():
    """Advance the running generator by one step."""
    try:
        with contextlib.redirect_stdout(S['buf']):
            next(S['gen'])
    except StopIteration as e:
        _finish({'ok': True, 'value': e.value})
    except Exception:
        _finish({'ok': False, 'traceback': traceback.format_exc()})


def _tick(dt):
    # Engine calls such as AssetTools.import_asset_tasks pump Slate ticks while they run, which re-enters this
    # callback mid-job ("generator already executing", seen live 2026-10-01). Ignore nested ticks.
    if S.get('busy'):
        return
    S['busy'] = True
    try:
        _tick_body()
    finally:
        S['busy'] = False


def _tick_body():
    now = time.time()
    if now - S['last_beat'] > 5.0:
        S['last_beat'] = now
        try:
            _atomic_write(RUN + '/bridge_heartbeat.json', {'t': now, 'job': S['stem']})
        except Exception:
            pass
    if S['gen'] is not None:
        _step()
    elif S['stem'] is None and now - S['last_poll'] > 0.5:
        S['last_poll'] = now
        _claim()


os.makedirs(QUEUE, exist_ok=True)
os.makedirs(RUN, exist_ok=True)
# A job left .running by a crashed session is reported, not silently retried.
for stale in glob.glob(QUEUE + '/*.running'):
    stem = os.path.basename(stale)[:-len('.running')]
    _atomic_write(f'{QUEUE}/{stem}.result.json', {'ok': False, 'traceback': 'bridge restarted while this job was running'})
    os.remove(stale)

_HANDLE = unreal.register_slate_post_tick_callback(_tick)
_atomic_write(RUN + '/bridge_ready.json', {
    'pid': os.getpid(),
    'engine': unreal.SystemLibrary.get_engine_version(),
    'throttle_off': _THROTTLE_OFF,
    't': time.time(),
})
unreal.log('[lt_bridge] ready')
