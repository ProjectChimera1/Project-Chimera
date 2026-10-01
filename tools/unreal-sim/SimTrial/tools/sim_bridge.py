"""Editor start script for A9: puts SimTrial/tools on sys.path, then runs the look test's ue_bridge.py unchanged.

    UnrealEditor.exe <uproject> -ExecutePythonScript=<this file>

The bridge then serves jobs queued by LookTest/tools/ue_job.py (queue in LookTest/queue); job modules in
LookTest/tools (lt_common, lt_import) and here (sim_import) are importable.
"""
import sys

SIMTOOLS = 'D:/Projects/Chimera-Unreal/ProjectChimera/SimTrial/tools'
if SIMTOOLS not in sys.path:
    sys.path.insert(0, SIMTOOLS)
_BRIDGE = 'D:/Projects/Chimera-Unreal/ProjectChimera/LookTest/tools/ue_bridge.py'
exec(compile(open(_BRIDGE, encoding='utf-8').read(), _BRIDGE, 'exec'), globals())
