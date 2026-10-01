// Copyright Epic Games, Inc. All Rights Reserved.

using UnrealBuildTool;

public class ChimeraHud : ModuleRules
{
	public ChimeraHud(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// V7 turns legacy include paths off: subfolders (Ui/, Data/, Render/, Game/) include relative to the module root.
		PublicIncludePaths.Add(ModuleDirectory);

		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine", "InputCore" });

		PrivateDependencyModuleNames.AddRange(new string[] { "Slate", "SlateCore", "ImageWrapper" });
	}
}
