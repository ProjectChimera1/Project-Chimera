// C10: rebuilds the sim ElevationGrid from a saved height.r32 with the shared sim code (FixedPoint, ElevationGrid,
// HeightmapCellMapping) and compares sim_grid_fnv + the 16 probes with the C++ values in terrain.json (next to height.r32).
// usage: elevhash <height.r32> <width> <half_extent_m>   exit 0 = all equal.
using System.Text.Json;
using ProjectChimera.Core;

if (args.Length < 3) { Console.Error.WriteLine("usage: elevhash height.r32 width half_extent"); return 2; }
string path = args[0]; int w = int.Parse(args[1]); int e = int.Parse(args[2]);
byte[] bytes = File.ReadAllBytes(path);
if (bytes.Length != w * w * 4) { Console.WriteLine($"FAIL size {bytes.Length} != {w * w * 4}"); return 1; }
float[] src = new float[w * w];
Buffer.BlockCopy(bytes, 0, src, 0, bytes.Length); // little-endian host
const int N = 256; int b = e - 128;
var heights = new Fixed[N * N];
for (int r = 0; r < N; r++)
    for (int c = 0; c < N; c++)
    {
        int vx = HeightmapCellMapping.CellToTexel(c, N, N) + b, vz = HeightmapCellMapping.CellToTexel(r, N, N) + b;
        heights[r * N + c] = Fixed.FromFloat(src[vz * w + vx]);
    }
var grid = new ElevationGrid(heights, N, N, Fixed.FromFloat(-128f), Fixed.FromFloat(-128f), Fixed.FromFloat(1f));
uint h = 2166136261;
foreach (var f in heights) for (int s = 0; s < 32; s += 8) { h ^= (uint)((f.Raw >> s) & 0xFF); h *= 16777619; }
string fnv = $"0x{h:x8}";
Console.WriteLine($"sim_grid_fnv {fnv}");

string tj = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "terrain.json");
using var doc = JsonDocument.Parse(File.ReadAllText(tj));
var root = doc.RootElement;
bool ok = true;
string want = root.GetProperty("sim_grid_fnv").GetString()!;
bool fnvOk = want == fnv; ok &= fnvOk;
Console.WriteLine($"BAR sim_grid_fnv {(fnvOk ? "PASS" : "FAIL")} csharp={fnv} cpp={want}");
int n = 0, mism = 0, negNonExact = 0, boundary = 0;
foreach (var q in root.GetProperty("probes").EnumerateArray())
{
    int sx = q.GetProperty("sim_x_raw").GetInt32(), sz = q.GetProperty("sim_z_raw").GetInt32(), want_raw = q.GetProperty("value_raw").GetInt32();
    int got = grid.Sample(Fixed.FromRaw(sx), Fixed.FromRaw(sz)).Raw;
    bool eq = got == want_raw; if (!eq) mism++;
    if (got < 0 && (got & 0xFFFF) != 0) negNonExact++;
    if ((sx & 0xFFFF) is 0 or 0xFFFF || (sz & 0xFFFF) is 0 or 0xFFFF) boundary++;
    Console.WriteLine($"probe {n++,2} x={sx,9} z={sz,9} csharp={got,8} cpp={want_raw,8} {(eq ? "ok" : "MISMATCH")}");
}
ok &= n == 16 && mism == 0;
Console.WriteLine($"BAR probes_16 {(n == 16 && mism == 0 ? "PASS" : "FAIL")} n={n} mismatches={mism} negative_non_exact={negNonExact} cell_boundary={boundary}");
string axes = root.GetProperty("axes").GetString()!;
bool axOk = axes == "sim(x,z,h)->ue_cm(100x,100z,100h)"; ok &= axOk;
Console.WriteLine($"BAR axes {(axOk ? "PASS" : "FAIL")} {axes}");
Console.WriteLine(ok ? "ELEVHASH PASS" : "ELEVHASH FAIL");
return ok ? 0 : 1;
