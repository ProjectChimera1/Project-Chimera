# Top up a MINIMAL CUDA Toolkit install with the headers torch's C++ extension build needs.
#
# WHY. Installing the CUDA network installer with a hand-picked component list keeps the download
# to a few hundred MB and — importantly — avoids touching the display driver, which is NEWER than
# the one the toolkit bundles and must not be downgraded. The cost is that several header-only
# pieces torch includes (crt/, cusparse, cusolver, ...) are simply absent, and each surfaces as a
# `fatal error C1083: Cannot open include file` one at a time, several minutes apart.
#
# NVIDIA publishes every component as a standalone redistributable archive, which is a far more
# precise instrument than the installer: these are ~0.1-30 MB each, contain only headers and libs,
# and cannot touch the driver.

$ErrorActionPreference = "Stop"

$CudaHome = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0"
$Index    = "https://developer.download.nvidia.com/compute/cuda/redist/redistrib_13.0.1.json"
$Base     = "https://developer.download.nvidia.com/compute/cuda/redist/"
$Tmp      = "D:\tools\cuda_redist"

# The set torch's ATen headers reach for on a CUDA build. cuda_crt is the one that is missing even
# though version.json claims it is installed.
$Want = @("cuda_crt", "cuda_cudart", "libcusparse", "libcusolver", "libcublas", "libcurand", "libcufft")

New-Item -ItemType Directory -Force -Path $Tmp | Out-Null
$j = (Invoke-WebRequest $Index -UseBasicParsing -TimeoutSec 60).Content | ConvertFrom-Json

foreach ($name in $Want) {
    $entry = $j.$name
    if (-not $entry) { Write-Output "skip $name (not in index)"; continue }
    $rel = $entry.'windows-x86_64'.relative_path
    if (-not $rel) { Write-Output "skip $name (no windows build)"; continue }

    $zip = Join-Path $Tmp ("{0}.zip" -f $name)
    if (-not (Test-Path $zip)) {
        Invoke-WebRequest ($Base + $rel) -OutFile $zip -UseBasicParsing -TimeoutSec 600
    }
    $dir = Join-Path $Tmp $name
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    Expand-Archive $zip -DestinationPath $dir -Force

    # Merge include/ and lib/x64 into the toolkit, never overwriting anything already correct.
    foreach ($sub in @("include", "lib\x64")) {
        $src = Get-ChildItem -Directory $dir | Select-Object -First 1
        $from = Join-Path $src.FullName $sub
        if (Test-Path $from) {
            $to = Join-Path $CudaHome $sub
            New-Item -ItemType Directory -Force -Path $to | Out-Null
            Copy-Item "$from\*" -Destination $to -Recurse -Force
        }
    }
    Write-Output ("merged {0,-14} {1}" -f $name, $entry.version)
}

Write-Output "--- header check ---"
foreach ($h in @("crt\host_config.h", "cusparse.h", "cusolverDn.h", "cublas_v2.h", "curand.h", "cufft.h")) {
    $ok = Test-Path (Join-Path "$CudaHome\include" $h)
    Write-Output ("  {0,-22} {1}" -f $h, $(if ($ok) { "OK" } else { "MISSING" }))
}
Write-Output "--- driver must be untouched ---"
nvidia-smi --query-gpu=driver_version --format=csv,noheader
