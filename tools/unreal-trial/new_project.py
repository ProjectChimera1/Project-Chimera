#!/usr/bin/env python3
"""new_project.py - scaffold an empty sibling Unreal project next to ProjectChimera (EXECUTION.md 2.1 S3).

Usage: new_project.py --name ChimeraHud|ChimeraTerrain [--root D:/Projects/Chimera-Unreal] [--force]

Writes the shared part once: <Name>.uproject (EngineAssociation copied from ProjectChimera, one Runtime module,
no plugins), Source/<Name>.Target.cs + <Name>Editor.Target.cs (V7, Unreal5_8), Source/<Name>/<Name>.Build.cs
(Core, CoreUObject, Engine, InputCore + PublicIncludePaths.Add(ModuleDirectory), as V7 turns legacy include paths off),
<Name>.h/.cpp (IMPLEMENT_PRIMARY_GAME_MODULE), Config/DefaultEngine.ini (ProjectChimera's renderer block, no
AndroidFileServer/SecurityToken section, both default maps /Engine/Maps/Entry), Config/DefaultGame.ini (new ProjectID).
Existing files are kept unless --force; existing folders such as HudRef/ or HudData/ are never touched.
"""
import argparse
import json
import os
import re
import sys
import uuid

NAMES = ("ChimeraHud", "ChimeraTerrain")
ENGINE_INI = """[/Script/EngineSettings.GameMapsSettings]
GameDefaultMap=/Engine/Maps/Entry
EditorStartupMap=/Engine/Maps/Entry

[/Script/Engine.RendererSettings]
r.AllowStaticLighting=False
r.Shadow.Virtual.Enable=1

r.GenerateMeshDistanceFields=True

r.DynamicGlobalIlluminationMethod=1

r.ReflectionMethod=1

r.SkinCache.CompileShaders=True

r.RayTracing=False

r.RayTracing.RayTracingProxies.ProjectEnabled=True

r.Substrate=True

r.Substrate.ProjectGBufferFormat=0

r.DefaultFeature.AutoExposure.ExtendDefaultLuminanceRange=True

r.DefaultFeature.LocalExposure.HighlightContrastScale=0.8

r.DefaultFeature.LocalExposure.ShadowContrastScale=0.8

[/Script/WindowsTargetPlatform.WindowsTargetSettings]
DefaultGraphicsRHI=DefaultGraphicsRHI_DX12
-D3D12TargetedShaderFormats=PCD3D_SM5
+D3D12TargetedShaderFormats=PCD3D_SM6
-D3D11TargetedShaderFormats=PCD3D_SM5
+D3D11TargetedShaderFormats=PCD3D_SM5
Compiler=Default
AudioSampleRate=48000
"""

TARGET = """// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;
using System.Collections.Generic;

public class {cls} : TargetRules
{{
	public {cls}(TargetInfo Target) : base(Target)
	{{
		Type = TargetType.{typ};
		DefaultBuildSettings = BuildSettingsVersion.V7;
		IncludeOrderVersion = EngineIncludeOrderVersion.Unreal5_8;
		ExtraModuleNames.Add("{name}");
	}}
}}
"""

BUILD = """// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class {name} : ModuleRules
{{
	public {name}(ReadOnlyTargetRules Target) : base(Target)
	{{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// V7 turns legacy include paths off: subfolders (Ui/, Data/, Render/, Game/) include relative to the module root.
		PublicIncludePaths.Add(ModuleDirectory);

		PublicDependencyModuleNames.AddRange(new string[] {{ "Core", "CoreUObject", "Engine", "InputCore" }});

		PrivateDependencyModuleNames.AddRange(new string[] {{ }});
	}}
}}
"""

HEADER = """// Copyright Epic Games, Inc. All Rights Reserved.

#pragma once

#include "CoreMinimal.h"
"""

CPP = """// Copyright Epic Games, Inc. All Rights Reserved.

#include "{name}.h"
#include "Modules/ModuleManager.h"

IMPLEMENT_PRIMARY_GAME_MODULE( FDefaultGameModuleImpl, {name}, "{name}" );
"""


def write(path, text, force):
    if os.path.exists(path) and not force:
        print("KEEP", path)
        return
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("WROTE", path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--name", required=True, choices=NAMES)
    ap.add_argument("--root", default="D:/Projects/Chimera-Unreal")
    ap.add_argument("--force", action="store_true")
    a = ap.parse_args()
    n, root = a.name, a.root
    with open(os.path.join(root, "ProjectChimera", "ProjectChimera.uproject"), encoding="utf-8") as f:
        assoc = json.load(f)["EngineAssociation"]
    proj = os.path.join(root, n)
    uproj = {"FileVersion": 3, "EngineAssociation": assoc, "Category": "", "Description": "",
             "Modules": [{"Name": n, "Type": "Runtime", "LoadingPhase": "Default"}],
             # AndroidFileServer writes a SecurityToken into Config/DefaultEngine.ini on the first editor-mode run
             # (found by C2, 2026-10-01); disabling it keeps secrets out of the committed config.
             "Plugins": [{"Name": "AndroidFileServer", "Enabled": False}]}
    write(os.path.join(proj, n + ".uproject"), json.dumps(uproj, indent="\t") + "\n", a.force)
    src = os.path.join(proj, "Source")
    write(os.path.join(src, n + ".Target.cs"), TARGET.format(cls=n + "Target", typ="Game", name=n), a.force)
    write(os.path.join(src, n + "Editor.Target.cs"), TARGET.format(cls=n + "EditorTarget", typ="Editor", name=n), a.force)
    write(os.path.join(src, n, n + ".Build.cs"), BUILD.format(name=n), a.force)
    write(os.path.join(src, n, n + ".h"), HEADER, a.force)
    write(os.path.join(src, n, n + ".cpp"), CPP.format(name=n), a.force)
    write(os.path.join(proj, "Config", "DefaultEngine.ini"), ENGINE_INI, a.force)
    pid = uuid.uuid4().hex.upper()
    write(os.path.join(proj, "Config", "DefaultGame.ini"),
          "\n[/Script/EngineSettings.GeneralProjectSettings]\nProjectID=" + pid + "\n", a.force)
    print("NEW_PROJECT OK", n)
    return 0


if __name__ == "__main__":
    sys.exit(main())
