#!/usr/bin/env bash
# Render every grayscale reference and control of plan B (task T0) into D:/Projects/Chimera-Unreal/ChimeraHud/HudRef/ref/.
# Needs: python (numpy, Pillow, scipy), playwright-cli, network on the very first run only (fetch_webfonts.py pins the fonts).
# No Unreal process is started, so no lock is needed. Afterwards run `python ref_check.py`.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && { pwd -W 2>/dev/null || pwd; })"
export MSYS_NO_PATHCONV=1
python "$HERE/ref_render.py"
# the 4x LCD-vs-grayscale text comparison is for Alec (plan B section 10, D6). EXECUTION 2.3: (b) images stay in their
# working folder and are REGISTERED in EV/b/manifest.json (absolute path + sha256), never copied into EV.
SRC="D:/Projects/Chimera-Unreal/ChimeraHud/HudRef/ref/lcd_vs_gray_text_4x.png"
python "$HERE/../unreal-trial/evidence.py" add --check b --register --task T0 --name b-T0-lcd-vs-gray-4x.png "$SRC"
echo "render_reference: done (for Alec: $SRC, registered as b-T0-lcd-vs-gray-4x.png)"
