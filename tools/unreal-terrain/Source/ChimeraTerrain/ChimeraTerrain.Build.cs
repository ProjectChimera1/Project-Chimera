// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class ChimeraTerrain : ModuleRules
{
	public ChimeraTerrain(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// V7 turns legacy include paths off: subfolders (Ui/, Data/, Render/, Game/) include relative to the module root.
		PublicIncludePaths.Add(ModuleDirectory);

		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine", "InputCore", "RenderCore", "RHI", "Slate", "SlateCore", "Json", "JsonUtilities", "RealtimeMeshComponent", "GeometryFramework", "GeometryCore" });

		PrivateDependencyModuleNames.AddRange(new string[] { });
	}
}
