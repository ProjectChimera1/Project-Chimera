"""Split a Phase 2 checkpoint marker's file list by repo; report status per path.

usage: python ckpt.py <task> [<task> ...]
Prints, per repo, the repo-relative paths (existing ones, with their git status) and any path outside both repos.
Writes <scratch>/ckpt_<repo>.txt (one path per line) for `git add --pathspec-from-file`.
"""
import json, os, subprocess, sys

MARK = 'D:/Projects/Chimera-Unreal/TrialOut/checkpoints'
REPOS = {'R': 'D:/Projects/Project_Chimera', 'U': 'D:/Projects/Chimera-Unreal'}
HERE = os.environ.get('CKPT_OUT', os.path.dirname(os.path.abspath(__file__)))

def norm(p):
    p = p.split(' (')[0].strip().replace('\\', '/')
    if len(p) > 1 and p[1] == ':':
        p = p[0].upper() + p[1:]
    return p

out = {k: [] for k in REPOS}
other = []
for task in sys.argv[1:]:
    m = json.load(open(f'{MARK}/{task}.json', encoding='utf-8'))
    print(f"== {task}: {m.get('status')} (impl {m.get('impl_status')})")
    for f in m.get('files', []):
        p = norm(f)
        for k, root in REPOS.items():
            if p.lower().startswith(root.lower() + '/'):
                out[k].append(p[len(root) + 1:])
                break
        else:
            other.append(p)
for k, paths in out.items():
    paths = sorted(set(paths))
    if not paths:
        continue
    print(f'-- {k} ({REPOS[k]}): {len(paths)} paths')
    st = subprocess.run(['git', '-C', REPOS[k], 'status', '--porcelain', '--'] + paths, capture_output=True, text=True).stdout
    print(st.rstrip() or '(no changes)')
    missing = [p for p in paths if not os.path.exists(os.path.join(REPOS[k], p))]
    if missing:
        print('missing on disk:', missing)
    with open(os.path.join(HERE, f'ckpt_{k}.txt'), 'w', encoding='utf-8', newline='\n') as fh:
        fh.write('\n'.join(p for p in paths if p not in missing) + '\n')
if other:
    print('-- outside both repos:', other)
