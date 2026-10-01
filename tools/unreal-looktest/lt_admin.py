"""Bridge housekeeping jobs: liveness ping and clean editor shutdown."""
import os
import time

import unreal


def ping():
    """Return engine version, pid and the editor world name, proving the bridge runs jobs."""
    world = unreal.get_editor_subsystem(unreal.UnrealEditorSubsystem).get_editor_world()
    return {'pid': os.getpid(), 'engine': unreal.SystemLibrary.get_engine_version(),
            'world': world.get_name() if world else None, 't': time.time()}


def quit():
    """Let the Python executer close the editor on its next tick (EditorPythonExecuter.cpp:71-77)."""
    unreal.EditorPythonScripting.set_keep_python_script_alive(False)
    return 'quit requested'
