// Copyright Chimera. ChimeraSimHost: hosts the NativeAOT sim library inside Unreal (check a; X1 smoke first).
using UnrealBuildTool;

public class ChimeraSimHost : ModuleRules
{
	public ChimeraSimHost(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// V7 turns legacy include paths off (ModuleRules.cs:1473-1487); keep the module root includable.
		PublicIncludePaths.Add(ModuleDirectory);

		// Deps per plan A 3.7 (Core, CoreUObject, Engine, RenderCore, RHI, Json); X1 only needs the first three.
		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine" });
	}
}
