// Project Chimera terrain trial (plan C scatter, task S2). Original Chimera code.
// Minimal stand-in for Unreal's CoreMinimal.h so the pure scatter generator compiles with plain cl.exe (standalone harness, never shipped).
#pragma once
#include <cstdint>
#include <cstring>
#include <cmath>
#include <vector>
#include <algorithm>
#include <memory>
#include <mutex>
#include <functional>
#include <cassert>
#include <utility>
#include <limits>
#include <cstdio>

#define CHIMERA_SCATTER_STANDALONE 1
#define WITH_DEV_AUTOMATION_TESTS 1
#include <string>
#include <cstdarg>
#define INDEX_NONE (-1)
#define check(x) assert(x)

typedef int8_t int8; typedef int16_t int16; typedef int32_t int32; typedef int64_t int64;
typedef uint8_t uint8; typedef uint16_t uint16; typedef uint32_t uint32; typedef uint64_t uint64;
typedef size_t SIZE_T;

enum class EAllowShrinking { No, Yes };
enum class ESPMode { ThreadSafe, NotThreadSafe };

template <class T> T MoveTemp(T& V) { return std::move(V); }
template <class T> T&& MoveTemp(T&& V) { return std::move(V); }

template <class T>
class TArray
{
public:
	std::vector<T> V;
	int32 Num() const { return (int32)V.size(); }
	int32 Add(const T& X) { V.push_back(X); return (int32)V.size() - 1; }
	int32 Add(T&& X) { V.push_back(std::move(X)); return (int32)V.size() - 1; }
	void Reset() { V.clear(); }
	void Empty() { V.clear(); }
	void Reserve(int32 N) { V.reserve(N); }
	void SetNum(int32 N) { V.resize(N); }
	void SetNumUninitialized(int32 N) { V.resize(N); }
	void Init(const T& X, int32 N) { V.assign(N, X); }
	T* GetData() { return V.data(); }
	const T* GetData() const { return V.data(); }
	T& operator[](int32 I) { assert(I >= 0 && I < (int32)V.size()); return V[I]; }
	const T& operator[](int32 I) const { assert(I >= 0 && I < (int32)V.size()); return V[I]; }
	T& AddDefaulted_GetRef() { V.emplace_back(); return V.back(); }
	T& Last() { return V.back(); }
	const T& Last() const { return V.back(); }
	T Pop(EAllowShrinking = EAllowShrinking::Yes) { T X = std::move(V.back()); V.pop_back(); return X; }
	void RemoveAt(int32 I) { V.erase(V.begin() + I); }
	void Shrink() { V.shrink_to_fit(); }
	bool operator==(const TArray& O) const { return V == O.V; }
	bool operator!=(const TArray& O) const { return V != O.V; }
	bool Contains(const T& X) const { return std::find(V.begin(), V.end(), X) != V.end(); }
	void Append(const TArray& O) { V.insert(V.end(), O.V.begin(), O.V.end()); }
	typename std::vector<T>::iterator begin() { return V.begin(); }
	typename std::vector<T>::iterator end() { return V.end(); }
	typename std::vector<T>::const_iterator begin() const { return V.begin(); }
	typename std::vector<T>::const_iterator end() const { return V.end(); }
};

template <class T, ESPMode M = ESPMode::ThreadSafe>
class TSharedPtr : public std::shared_ptr<T>
{
public:
	TSharedPtr() {}
	template <class U> TSharedPtr(const std::shared_ptr<U>& O) : std::shared_ptr<T>(O) {}
	template <class U, ESPMode M2> TSharedPtr(const TSharedPtr<U, M2>& O) : std::shared_ptr<T>(O) {}
	bool IsValid() const { return (bool)*this; }
	void Reset() { std::shared_ptr<T>::reset(); }
};
template <class T, ESPMode M = ESPMode::ThreadSafe, class... A>
TSharedPtr<T, M> MakeShared(A&&... Args) { return TSharedPtr<T, M>(std::make_shared<T>(std::forward<A>(Args)...)); }

template <class F> using TFunction = std::function<F>;

class FCriticalSection { public: std::mutex M; };
class FScopeLock { std::lock_guard<std::mutex> L; public: FScopeLock(FCriticalSection* C) : L(C->M) {} };

struct FMath
{
	template <class T> static T Min(T A, T B) { return A < B ? A : B; }
	template <class T> static T Max(T A, T B) { return A > B ? A : B; }
	template <class T> static T Clamp(T V, T Lo, T Hi) { return V < Lo ? Lo : (V > Hi ? Hi : V); }
	template <class T> static T Abs(T V) { return V < 0 ? -V : V; }
	static double FloorToDouble(double V) { return std::floor(V); }
	static int32 FloorToInt(float V) { return (int32)std::floor(V); }
	static float Sqrt(float V) { return std::sqrt(V); }
	static float Lerp(float A, float B, float T) { return A + (B - A) * T; }
};
struct FMemory
{
	static void Memset(void* D, int V, SIZE_T N) { std::memset(D, V, N); }
	static int Memcmp(const void* A, const void* B, SIZE_T N) { return std::memcmp(A, B, N); }
	static void Memcpy(void* D, const void* S, SIZE_T N) { std::memcpy(D, S, N); }
};
template <class T> struct TNumericLimits : std::numeric_limits<T> { static T Max() { return std::numeric_limits<T>::max(); } };

struct FVector3f
{
	float X, Y, Z;
	FVector3f(float x = 0, float y = 0, float z = 0) : X(x), Y(y), Z(z) {}
	FVector3f GetSafeNormal() const { float L = std::sqrt(X * X + Y * Y + Z * Z); return L > 1e-8f ? FVector3f(X / L, Y / L, Z / L) : FVector3f(0, 0, 0); }
};

// ---- string and automation test shim (tests only) ----
#define TEXT(x) x
#define ANSI_TO_TCHAR(x) (x)
typedef char TCHAR;
namespace ESearchCase { enum Type { CaseSensitive, IgnoreCase }; }
namespace ESearchDir { enum Type { FromStart, FromEnd }; }
class FString
{
public:
	std::string S;
	FString() {}
	FString(const char* P) : S(P ? P : "") {}
	FString(const std::string& P) : S(P) {}
	const char* operator*() const { return S.c_str(); }
	int32 Len() const { return (int32)S.size(); }
	bool IsEmpty() const { return S.empty(); }
	char operator[](int32 I) const { return S[I]; }
	static FString Printf(const char* Fmt, ...)
	{
		char Buf[4096];
		va_list A;
		va_start(A, Fmt);
		vsnprintf(Buf, sizeof(Buf), Fmt, A);
		va_end(A);
		return FString(Buf);
	}
	FString operator+(const FString& O) const { return FString(S + O.S); }
	FString& operator+=(const FString& O) { S += O.S; return *this; }
	void AppendChar(char C) { S.push_back(C); }
	bool operator==(const FString& O) const { return S == O.S; }
	bool operator==(const char* O) const { return S == O; }
	int32 Find(const char* Sub, ESearchCase::Type = ESearchCase::IgnoreCase, ESearchDir::Type = ESearchDir::FromStart, int32 Start = -1) const
	{
		size_t P = S.find(Sub, Start < 0 ? 0 : (size_t)Start);
		return P == std::string::npos ? -1 : (int32)P;
	}
	bool Contains(const char* Sub, ESearchCase::Type = ESearchCase::IgnoreCase) const { return S.find(Sub) != std::string::npos; }
	FString Mid(int32 Start, int32 Count) const { return FString(S.substr((size_t)Start, (size_t)Count)); }
};
struct FCString { static double Atod(const char* P) { return atof(P); } };
struct FPaths
{
	// The project root, derived from this header's own path (<project>/Tools/scatter_harness/shim/CoreMinimal.h).
	static FString ProjectDir()
	{
		std::string F = __FILE__;
		std::replace(F.begin(), F.end(), static_cast<char>(92), '/');
		const size_t P = F.find("Tools/scatter_harness/");
		return FString(P == std::string::npos ? std::string("./") : F.substr(0, P));
	}
};
struct FFileHelper
{
	static bool LoadFileToString(FString& Out, const char* Path)
	{
		FILE* F = fopen(Path, "rb");
		if (!F) return false;
		std::string R; char B[65536]; size_t N;
		while ((N = fread(B, 1, sizeof(B), F)) > 0) R.append(B, N);
		fclose(F);
		Out.S = R;
		return true;
	}
};
namespace EAutomationTestFlags { enum Type { ProductFilter = 1 }; }
#define EAutomationTestFlags_ApplicationContextMask 0
class FAutomationTestBase
{
public:
	std::string Name;
	std::vector<std::string> Errors;
	std::vector<std::string> Infos;
	virtual bool RunTest(const FString& Parameters) = 0;
	void AddError(const FString& S) { Errors.push_back(S.S); }
	void AddInfo(const FString& S) { Infos.push_back(S.S); }
	virtual ~FAutomationTestBase() {}
};
inline std::vector<FAutomationTestBase*>& GScTests() { static std::vector<FAutomationTestBase*> V; return V; }
#define IMPLEMENT_SIMPLE_AUTOMATION_TEST(TClass, PrettyName, Flags) 	class TClass : public FAutomationTestBase { public: TClass() { Name = PrettyName; GScTests().push_back(this); } bool RunTest(const FString& Parameters) override; }; 	static TClass TClass##Instance;
