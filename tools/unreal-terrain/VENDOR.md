# Vendored RealtimeMeshComponent (RMC)

- Upstream: https://github.com/TriAxis-Games/RealtimeMeshComponent (MIT)
- Pinned commit: `b8669a0891b728d8fa8a31f43f76d3853c10f743` (GitHub master, 2026-08-23; the `v5.3.2` tag has no 5.8 guards)
- Location: `Plugins/RealtimeMeshComponent/` is a nested git clone, git-ignored by U/.gitignore. It is NOT mirrored to the Chimera repo, so the
  patches below are the only record. Re-create: clone, `git checkout b8669a0891b728d8fa8a31f43f76d3853c10f743`, apply the diff below.
- Check: `git -C Plugins/RealtimeMeshComponent rev-parse HEAD` is the hash above; `git -C Plugins/RealtimeMeshComponent diff --stat` lists
  exactly `RealtimeMeshComponent.uplugin` and `Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp`.
- Line endings: `RealtimeMeshManaged.cpp` is CRLF, the `.uplugin` is LF; keep them.

## Patch 1: examples module not built in Game targets (plan C 3.1)
`RealtimeMeshComponent.uplugin`: `"TargetDenyList": ["Game"]` added to the `RealtimeMeshExamples` module descriptor (upstream `ModuleDescriptor.cs:204,370`).

## Patch 2: keep custom complex collision geometry (plan C 3.6, 2.2)
In `b8669a0`, `FRealtimeMeshManaged::GenerateComplexCollision` copies `ComplexGeometry` but returns LOD0's result, false when no section has collision;
the caller then discards the custom geometry. The patch returns true when custom geometry exists. Proved by the C5 automation test.

## Full diff (`git -C Plugins/RealtimeMeshComponent diff`)
```diff
diff --git a/RealtimeMeshComponent.uplugin b/RealtimeMeshComponent.uplugin
index d37a5e8..b111e41 100644
--- a/RealtimeMeshComponent.uplugin
+++ b/RealtimeMeshComponent.uplugin
@@ -31,7 +31,8 @@
 			"Name": "RealtimeMeshExamples",
 			"Type": "Runtime",
 			"LoadingPhase": "Default",
-			"PlatformDenyList": []
+			"PlatformDenyList": [],
+			"TargetDenyList": ["Game"]
 		}
 	]
 }
diff --git a/Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp b/Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp
index abf52e0..07cb0b1 100644
--- a/Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp
+++ b/Source/RealtimeMeshComponent/Private/RealtimeMeshManaged.cpp
@@ -466,9 +466,12 @@ namespace RealtimeMesh
 		// TODO: Allow other LOD to be used for collision?
 		if (LODs.IsValidIndex(0))
 		{
-			return StaticCastSharedRef<FRealtimeMeshLODManaged>(LODs[0])->GenerateComplexCollision(LockContext, OutComplexGeometry);
+			const bool bLod = StaticCastSharedRef<FRealtimeMeshLODManaged>(LODs[0])->GenerateComplexCollision(LockContext, OutComplexGeometry);
+			// Chimera patch: keep custom complex geometry even when no section has collision.
+			return bLod || OutComplexGeometry.NumMeshes() > 0;
 		}
-		return false;
+		// Chimera patch: custom geometry alone is a valid collision source.
+		return OutComplexGeometry.NumMeshes() > 0;
 	}
 
 	void FRealtimeMeshManaged::InitializeProxy(FRealtimeMeshUpdateContext& UpdateContext) const
```

## Compile-fix patches (C1)
None needed: RMC b8669a0 compiled unmodified beyond the two C0 patches on 5.8.3 (Editor and Game). Only deprecation warnings (see C1 record below).

## C1 build record (2026-10-01, UE 5.8.3, Win64 Development)
- `Tools/build.ps1 -Target Editor`: first attempt hit the 30 min watchdog at 24/83 actions (machine shared with other checks' jobs; no errors, log `Saved/c1_editor_try1.log`); rerun with `-WatchdogMin 90`: `Result: Succeeded`, 928 s UBT time, 4 warnings.
- `Tools/build.ps1 -Target Game`: `Result: Succeeded`, 352 s UBT time, 11 warning lines (all in RMC, deprecations), `Saved/c1_game.log`.
- Warnings are all deprecations in RMC (C4996 `UseGPUScene` x4 in RealtimeMeshComponentProxy/DebugVertexFactory; `FRayTracingGeometry::Initializer`, `GetProjectionMatrix` in the proxy), plus CS0618 `Log.TraceInformation` in RealtimeMeshComponent.Build.cs. No compile patches were needed.
