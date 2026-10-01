// Copyright Chimera. ChimeraSimHost: hosts the NativeAOT sim library (ChimeraSim.dll) inside Unreal (check a; plan A 3.7, R3 3.1).
using System.IO;
using UnrealBuildTool;

public class ChimeraSimHost : ModuleRules
{
	public ChimeraSimHost(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		// V7 turns legacy include paths off (ModuleRules.cs:1473-1487); keep the module root includable.
		PublicIncludePaths.Add(ModuleDirectory);

		// Deps per plan A 3.7 (Core, CoreUObject, Engine, RenderCore, RHI, Json).
		PublicDependencyModuleNames.AddRange(new string[] { "Core", "CoreUObject", "Engine", "RenderCore", "RHI", "Json" });

		// The sim library is consumed through GetDllExport only (no import lib, no delay-load; R3 3.1). Staged in place by
		// NAT/publish.ps1 -StageOnly; RuntimeDependencies stages it at the same project-relative path in a package (UNVERIFIED, plan A 7).
		RuntimeDependencies.Add("$(ProjectDir)/Binaries/ThirdParty/ChimeraSim/Win64/ChimeraSim.dll");
	}
}
