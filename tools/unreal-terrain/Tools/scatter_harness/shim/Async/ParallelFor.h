// Project Chimera terrain trial (plan C scatter, task S2). Original Chimera code. Standalone-harness stand-in for the engine header of the same name.
#pragma once
#include "CoreMinimal.h"
inline void ParallelFor(int32 Num, const std::function<void(int32)>& Body)
{
	for (int32 I = 0; I < Num; ++I) { Body(I); }
}
