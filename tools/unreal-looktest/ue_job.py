"""Host side of the editor job bridge: queue one job for ue_bridge.py and wait for its result.

    python ue_job.py <module> [func=run] ['{json args}'] [--timeout 1800]

Writes queue/<YYYYMMDD-HHMMSS-mmm>_<module>.job.json (tmp + os.replace, so the editor never
sees a half-written file; the timestamp prefix makes the bridge's sorted() pick the oldest
first), then polls queue/<stem>.result.json once a second. Prints value, stdout and any
traceback. Exit 0 on ok:true, 1 on ok:false, a timeout, an interrupt, or bad arguments. On a timeout
or Ctrl-C a job the editor has not claimed yet is withdrawn, so it cannot run later in some other session.
"""
import argparse
import datetime
import json
import os
import sys
import time

LT = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest'
QUEUE = LT + '/queue'


def withdraw(job_path, running_path, result_path):
    """Remove a queued job nobody claimed and say what happened to it."""
    try:
        os.remove(job_path)
        return 'job withdrawn, it never started'
    except FileNotFoundError:
        pass
    if os.path.exists(running_path):
        return f'job is still running in the editor; its result will land at {result_path}'
    return f'job already finished; result at {result_path}'


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('module', help='editor-side module in LookTest/tools, e.g. lt_probe')
    ap.add_argument('func', nargs='?', default='run', help='function to call (default run)')
    ap.add_argument('args', nargs='?', default='{}', help='JSON object of keyword arguments')
    ap.add_argument('--timeout', type=float, default=1800, help='seconds to wait for the result (default 1800)')
    a = ap.parse_args()

    try:
        job_args = json.loads(a.args)
    except json.JSONDecodeError as e:
        print(f'ue_job: args is not valid JSON: {e}', file=sys.stderr)
        return 1
    if not isinstance(job_args, dict):
        print('ue_job: args must be a JSON object', file=sys.stderr)
        return 1
    if not os.path.isdir(QUEUE):
        print(f'ue_job: queue folder missing: {QUEUE}', file=sys.stderr)
        return 1

    now = datetime.datetime.now()
    stem = f'{now:%Y%m%d-%H%M%S}-{now.microsecond // 1000:03d}_{a.module}'
    job_path = f'{QUEUE}/{stem}.job.json'
    result_path = f'{QUEUE}/{stem}.result.json'
    tmp = job_path + '.tmp'
    with open(tmp, 'w', encoding='utf-8') as f:
        json.dump({'module': a.module, 'func': a.func, 'args': job_args}, f)
    os.replace(tmp, job_path)
    print(f'ue_job: queued {stem} ({a.module}.{a.func})', flush=True)

    running_path = f'{QUEUE}/{stem}.running'
    deadline = time.time() + a.timeout
    try:
        while not os.path.exists(result_path):
            if time.time() > deadline:
                print(f'ue_job: TIMEOUT after {a.timeout:.0f}s waiting for {result_path}: '
                      f'{withdraw(job_path, running_path, result_path)}', file=sys.stderr)
                return 1
            time.sleep(1)
    except KeyboardInterrupt:
        print(f'ue_job: interrupted: {withdraw(job_path, running_path, result_path)}', file=sys.stderr)
        return 1
    # The bridge writes the result with os.replace, so a visible file is complete.
    with open(result_path, encoding='utf-8') as f:
        res = json.load(f)

    if res.get('stdout'):
        print('--- stdout ---')
        print(res['stdout'].rstrip())
    if res.get('ok'):
        print(f'--- ok in {res.get("secs")}s ---')
        print(json.dumps(res.get('value'), indent=1, default=repr))
        return 0
    print('--- FAILED ---', file=sys.stderr)
    print(res.get('traceback', json.dumps(res)), file=sys.stderr)
    return 1


if __name__ == '__main__':
    sys.exit(main())
