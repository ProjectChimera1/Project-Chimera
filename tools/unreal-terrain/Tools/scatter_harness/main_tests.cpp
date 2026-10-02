// Project Chimera terrain trial (plan C scatter, task S2). Original Chimera code.
// Runner of the standalone scatter harness (build_and_run.bat): every pure Chimera.Terrain.Scatter test, optional name filter.
#include "CoreMinimal.h"
#include <chrono>
int main(int argc, char** argv)
{
	std::string Filter = argc > 1 ? argv[1] : "";
	int Pass = 0, Fail = 0;
	for (FAutomationTestBase* T : GScTests())
	{
		if (!Filter.empty() && T->Name.find(Filter) == std::string::npos) continue;
		auto t0 = std::chrono::steady_clock::now();
		bool Ok = T->RunTest(FString());
		double Ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count();
		bool Good = Ok && T->Errors.empty();
		printf("%s %-60s %8.0f ms\n", Good ? "PASS" : "FAIL", T->Name.c_str(), Ms);
		for (auto& I : T->Infos) printf("      info: %s\n", I.c_str());
		for (size_t i = 0; i < T->Errors.size() && i < 10; ++i) printf("      ERROR: %s\n", T->Errors[i].c_str());
		if (T->Errors.size() > 10) printf("      ... %zu errors\n", T->Errors.size());
		(Good ? Pass : Fail)++;
	}
	printf("TESTS pass=%d fail=%d\n", Pass, Fail);
	return Fail ? 1 : 0;
}
