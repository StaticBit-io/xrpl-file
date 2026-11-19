#!/usr/bin/env bash
# Writes the stand-in files used to build and rehearse the examples page before the owner's own
# files exist: two drawings and a 6 s clip, all generated from ffmpeg test patterns.
#
#   tools/examples/make-stand-ins.sh <dir>
#
# Nothing here is derived from the testnet fixtures. The large drawing is deliberately above
# 1,000,000 bytes as PNG so that prepare.sh takes its WebP conversion path, and the clip keeps
# ffmpeg's default layout (moov after mdat) so that the +faststart remux path runs on a real publish.
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: make-stand-ins.sh <dir>" >&2; exit 2; }
out="$1"
mkdir -p "$out"

# Git Bash would rewrite the drive path inside the drawtext option.
export MSYS2_ARG_CONV_EXCL='*'

ffmpeg_quiet=(ffmpeg -y -hide_banner -loglevel error -nostdin)

# The caption is a nicety: drawtext needs a font file, and a build without the filter just skips it.
caption() {
  local text="$1" size="$2" font=""
  for candidate in /c/Windows/Fonts/arial.ttf /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf /Library/Fonts/Arial.ttf; do
    [ -f "$candidate" ] && { font="$candidate"; break; }
  done
  if [ -n "$font" ] && ffmpeg -hide_banner -filters 2>/dev/null | grep -q ' drawtext '; then
    local path="$font"
    case "$path" in /c/*) path='C\\:/'"${path#/c/}" ;; esac
    printf ',drawtext=fontfile=%s:text=%s:fontsize=%s:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=12:x=(w-text_w)/2:y=(h-text_h)/2' "$path" "$text" "$size"
  fi
}

"${ffmpeg_quiet[@]}" -f lavfi -i "testsrc2=size=800x600:rate=1" -frames:v 1 \
  -vf "format=rgb24$(caption STAND-IN 64)" "$out/stand-in-drawing-small.png"

# Temporal noise keeps the PNG large; the amount is tuned to land near 1.5 MB.
"${ffmpeg_quiet[@]}" -f lavfi -i "testsrc2=size=1280x960:rate=1" -frames:v 1 \
  -vf "format=rgb24,noise=alls=3:allf=t$(caption STAND-IN 96)" "$out/stand-in-drawing-large.png"

"${ffmpeg_quiet[@]}" -f lavfi -i "testsrc2=size=640x360:rate=25:duration=6" \
  -f lavfi -i "sine=frequency=440:duration=6" \
  -vf "format=yuv420p$(caption STAND-IN 48)" -c:v libx264 -preset veryfast -crf 28 -pix_fmt yuv420p \
  -c:a aac -b:a 64k -shortest "$out/stand-in-clip.mp4"

ls -l "$out"
