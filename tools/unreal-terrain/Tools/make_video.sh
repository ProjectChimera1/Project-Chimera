#!/usr/bin/env bash
# make_video.sh <run_dir> - the sculpt video of a VIDEO run (plan C 4 C9): collects the MovieFrame*.png the run's results.json lists (the screenshot dir, as
# the director recorded it) and encodes <run_dir>/sculpt.mp4 (h264, 1280x720, 30 fps) and <run_dir>/sculpt.gif (640 px wide, 15 fps, <= 8 MB).
# Prints one MP4 line and one GIF line (duration, bytes) and exits 0 when both exist and the GIF fits at 640 px / 15 fps (3 = fits only below spec). Light work (no lock).
set -euo pipefail
RUN="${1:?usage: make_video.sh <run_dir>}"
FFMPEG="${FFMPEG:-C:/Users/MD_Ki/AppData/Local/Microsoft/WinGet/Links/ffmpeg.exe}"
FFPROBE="${FFPROBE:-C:/Users/MD_Ki/AppData/Local/Microsoft/WinGet/Links/ffprobe.exe}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GIF_MAX=$((8 * 1024 * 1024))
STAGE="$RUN/_frames"
python "$HERE/make_video_frames.py" "$RUN" "$STAGE"
# MP4: 330 frames at 30 fps = 11 s; yuv420p so every player opens it.
"$FFMPEG" -y -loglevel error -framerate 30 -i "$STAGE/f%05d.png" -vf "scale=1280:720:flags=lanczos" \
  -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p -movflags +faststart "$RUN/sculpt.mp4"
# GIF: a ladder from the nicest to the smallest setting; stop at the first that fits 8 MB.
made=0
for cfg in "15 640 256 bayer:bayer_scale=5" "15 640 128 bayer:bayer_scale=5" "15 576 128 bayer:bayer_scale=5" "12 512 96 none"; do
  set -- $cfg
  fps=$1; w=$2; cols=$3; dither=$4
  "$FFMPEG" -y -loglevel error -framerate 30 -i "$STAGE/f%05d.png" \
    -vf "fps=$fps,scale=$w:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=$cols:stats_mode=diff[p];[b][p]paletteuse=dither=$dither:diff_mode=rectangle" \
    -loop 0 "$RUN/sculpt.gif"
  bytes=$(stat -c %s "$RUN/sculpt.gif")
  echo "gif try fps=$fps width=$w colors=$cols dither=$dither bytes=$bytes"
  if [ "$bytes" -le "$GIF_MAX" ]; then made=1; break; fi
done
spec=1
[ "$fps" = "15" ] && [ "$w" = "640" ] || spec=0
rm -rf "$STAGE"
dur=$("$FFPROBE" -v error -show_entries format=duration -of default=nw=1:nk=1 "$RUN/sculpt.mp4")
echo "MP4 $RUN/sculpt.mp4 duration_s=$dur bytes=$(stat -c %s "$RUN/sculpt.mp4")"
echo "GIF $RUN/sculpt.gif bytes=$(stat -c %s "$RUN/sculpt.gif") max=$GIF_MAX"
[ "$made" -eq 1 ] || { echo "GIF does not fit 8 MB"; exit 1; }
# plan C 4 C9 asks for 640 px at 15 fps: a smaller rung still writes a GIF but is below spec (exit 3, never a silent pass)
[ "$spec" -eq 1 ] || { echo "GIF BELOW SPEC: fps=$fps width=$w (plan C wants 640 px, 15 fps)"; exit 3; }
echo "GIF SPEC OK fps=$fps width=$w colors=$cols"
