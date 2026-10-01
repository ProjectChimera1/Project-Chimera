"""SSIM for the HUD comparator (plan B section 4.3 G7): luma, Gaussian window sigma 1.5, K1 .01, K2 .03.

Wang et al. 2004 with the standard 11-tap Gaussian (truncate 3.5 -> radius 5), population (biased) variances and
'nearest' edge handling so that constant images stay constant at the borders. Data range 255.

Closed form used by the tests: for two constant images a and b the structure/contrast term is C2/C2 = 1 and
SSIM = (2ab + C1) / (a^2 + b^2 + C1).
"""
from __future__ import annotations

import numpy as np
from scipy.ndimage import gaussian_filter

SIGMA = 1.5
TRUNCATE = 3.5          # radius = int(3.5 * 1.5 + 0.5) = 5 -> 11 taps
K1, K2 = 0.01, 0.03
L = 255.0
C1 = (K1 * L) ** 2
C2 = (K2 * L) ** 2
RADIUS = 5


def _blur(a: np.ndarray) -> np.ndarray:
    return gaussian_filter(a, sigma=SIGMA, mode="nearest", truncate=TRUNCATE)


def ssim_map(x: np.ndarray, y: np.ndarray) -> np.ndarray:
    """Per-pixel SSIM of two 2-D arrays (luma, 0..255)."""
    x = np.asarray(x, dtype=np.float64)
    y = np.asarray(y, dtype=np.float64)
    if x.shape != y.shape or x.ndim != 2:
        raise ValueError(f"ssim_map needs two equal 2-D arrays, got {x.shape} and {y.shape}")
    mx, my = _blur(x), _blur(y)
    sxx = _blur(x * x) - mx * mx
    syy = _blur(y * y) - my * my
    sxy = _blur(x * y) - mx * my
    num = (2 * mx * my + C1) * (2 * sxy + C2)
    den = (mx * mx + my * my + C1) * (sxx + syy + C2)
    return num / den


def ssim(x, y, mask=None) -> float:
    """Mean SSIM, optionally over a boolean mask (pixels where mask is True)."""
    m = ssim_map(x, y)
    if mask is None:
        return float(m.mean())
    mask = np.asarray(mask, dtype=bool)
    return float(m[mask].mean()) if mask.any() else float("nan")


def closed_form_constant(a: float, b: float) -> float:
    """SSIM of two constant images with grey levels a and b."""
    return (2 * a * b + C1) / (a * a + b * b + C1)
