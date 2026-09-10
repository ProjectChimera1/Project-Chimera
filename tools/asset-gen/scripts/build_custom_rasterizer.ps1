# Build `custom_rasterizer` from source for THIS GPU.
#
# WHY THIS IS NEEDED. kijai ships prebuilt wheels and one of them matches this rig's
# torch/CUDA/Python exactly (torch2100.cuda130-cp312), so it INSTALLS and IMPORTS cleanly -- an
# import check passes and looks like success. It then fails at the first kernel launch with
# `CUDA error: no kernel image is available for execution on the device`, because the wheel was
# not compiled for the RTX 3060's compute capability (sm_86). The error surfaces asynchronously,
# several frames later inside a pure-torch indexing expression, so it does not point at the
# rasterizer at all.
#
# So: import success is NOT evidence the extension works. Only a kernel launch is.
#
# Requires: nvcc (CUDA Toolkit) and MSVC. Both are installed but neither is on PATH by default,
# which is why this script locates them rather than assuming a developer prompt.

$ErrorActionPreference = "Stop"

$Venv    = if ($env:CHIMERA_HY3D_PY) { Split-Path (Split-Path $env:CHIMERA_HY3D_PY) } else { "D:\tools\hy3dpaint-venv" }
$Py      = Join-Path $Venv "Scripts\python.exe"
$Src     = "D:\tools\hy3d20\hy3dgen\texgen\custom_rasterizer"
$CudaHome = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.0"

if (-not (Test-Path $Py))   { throw "paint venv python not found: $Py" }
if (-not (Test-Path $Src))  { throw "rasterizer source not found: $Src" }
if (-not (Test-Path "$CudaHome\bin\nvcc.exe")) { throw "nvcc not found under $CudaHome" }

# Locate MSVC through vswhere rather than hardcoding a version that will age out.
$vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
$vsRoot = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw "Visual Studio C++ tools not found" }
$vcvars = Join-Path $vsRoot "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

Write-Output "venv : $Venv"
Write-Output "cuda : $CudaHome"
Write-Output "msvc : $vsRoot"

# 8.6 is the RTX 3060. Setting this explicitly is the entire point of building from source --
# torch would otherwise guess from the visible device, which is right here but fragile.
$env:TORCH_CUDA_ARCH_LIST = if ($env:TORCH_CUDA_ARCH_LIST) { $env:TORCH_CUDA_ARCH_LIST } else { "8.6" }
$env:CUDA_HOME = $CudaHome
$env:CUDA_PATH = $CudaHome
# nvcc 13.0 refuses MSVC 19.5x as "unsupported" even though it compiles correctly.
$env:NVCC_APPEND_FLAGS = "-allow-unsupported-compiler"

& $Py -m pip uninstall -y custom_rasterizer 2>&1 | Select-Object -Last 1

# INJECT -allow-unsupported-compiler INTO setup.py, idempotently.
#
# nvcc 13.0 hard-refuses MSVC 14.5x ("Only the versions between 2019 and 2022 (inclusive) are
# supported") and this rig has VS Build Tools 2026. The documented override is a compiler flag, and
# NVCC_APPEND_FLAGS / NVCC_PREPEND_FLAGS in the environment do NOT reach the command line that
# torch's ninja backend builds -- verified by reading the emitted nvcc invocation in the build log,
# where the flag is simply absent. setup.py already appends a Windows-only nvcc arg, so the flag
# goes in beside it, which is the one place that provably lands.
$setup = Join-Path $Src "setup.py"
$txt = Get-Content $setup -Raw
if ($txt -notmatch "allow-unsupported-compiler") {
    $txt = $txt -replace "nvcc_args \+= \['-Xcompiler', '/Zc:preprocessor'\]",
        "nvcc_args += ['-Xcompiler', '/Zc:preprocessor']`n    nvcc_args += ['-allow-unsupported-compiler']"
    Set-Content $setup $txt -NoNewline
    Write-Output "patched setup.py with -allow-unsupported-compiler"
} else {
    Write-Output "setup.py already carries -allow-unsupported-compiler"
}

# DISTUTILS_USE_SDK=1 is REQUIRED once vcvars64 has been sourced: torch's _check_abi refuses to
# build inside an already-activated VC environment without it, to avoid activating it twice.
# ORDER MATTERS, and not for a stylistic reason. In a cmd `&&` chain every `%VAR%` is expanded
# when the LINE is parsed, before any of it runs -- so a `set PATH=...;%PATH%` placed after
# `call vcvars64` substitutes the PRE-vcvars PATH and silently discards everything vcvars just
# added, leaving cl.exe unfindable while the MSVC *include* paths still look correct in the log.
# So CUDA goes on PATH first and vcvars prepends MSVC to it afterwards.
$cmd = "set `"PATH=$CudaHome\bin;%PATH%`" && " +
       "call `"$vcvars`" >nul && " +
       "set CUDA_HOME=$CudaHome&& set CUDA_PATH=$CudaHome&& " +
       "set DISTUTILS_USE_SDK=1&& " +
       "set TORCH_CUDA_ARCH_LIST=$($env:TORCH_CUDA_ARCH_LIST)&& " +
       "set NVCC_APPEND_FLAGS=-allow-unsupported-compiler&& " +
       "cd /d `"$Src`" && `"$Py`" -m pip install --no-build-isolation ."
Write-Output "--- building (this takes a few minutes) ---"
cmd.exe /c $cmd

Write-Output "--- verifying an actual KERNEL LAUNCH, not just an import ---"
& $Py -c @"
import torch, custom_rasterizer_kernel as k
print('import OK', torch.cuda.get_device_name(0), torch.cuda.get_device_capability(0))
pos = torch.tensor([[[-0.5,-0.5,0.5,1.0],[0.5,-0.5,0.5,1.0],[0.0,0.5,0.5,1.0]]], dtype=torch.float32, device='cuda')
tri = torch.tensor([[0,1,2]], dtype=torch.int32, device='cuda')
f, b = k.rasterize_image(pos[0], tri, torch.zeros(1, device='cuda'), 64, 64, 1e-6, 0)
torch.cuda.synchronize()
print('KERNEL LAUNCH OK  findices', tuple(f.shape), 'hit', int((f>0).sum()))
"@
