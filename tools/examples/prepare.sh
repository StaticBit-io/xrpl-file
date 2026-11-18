#!/usr/bin/env bash
# Turns the owner's raw drawings and video into the files published on the mainnet examples page.
#
#   tools/examples/prepare.sh [--cover-at <seconds>] <owner-dir> <out-dir>
#
# <owner-dir> holds the drawings (png, jpg, jpeg, webp, gif, bmp, tif, tiff), exactly one video
# (mp4, mov, m4v) and optionally cover.webp / cover.jpg / cover.jpeg. <out-dir> receives the
# publishable files and prepared.json, a JSON array of { file, type, bytes, width, height, role }
# that make-plan.mjs turns into the publication plan and the site reads for sizes and aspect ratios.
#
# A drawing the owner made as PNG, JPEG or WebP and that already fits in 1,000,000 bytes is copied
# byte for byte: the bytes are the owner's work, and re-encoding them is not ours to do. Anything
# else becomes WebP. The video must be H.264/AAC; it is remuxed with +faststart, never re-encoded,
# when its moov box is not before mdat. Each failed check stops the script.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
boxes_tool="$here/../fixtures/top-boxes.mjs"

drawing_max=1000000
cover_max=65536
cover_long_side=960

cover_at=""
args=()
while [ "$#" -gt 0 ]; do
  case "$1" in
    --cover-at)
      [ "$#" -ge 2 ] || { echo "--cover-at needs a number of seconds" >&2; exit 2; }
      cover_at="$2"; shift 2 ;;
    *) args+=("$1"); shift ;;
  esac
done
if [ "${#args[@]}" -ne 2 ]; then
  echo "usage: prepare.sh [--cover-at <seconds>] <owner-dir> <out-dir>" >&2
  exit 2
fi
owner="${args[0]}"
out="${args[1]}"

if [ -n "$cover_at" ] && ! [[ "$cover_at" =~ ^[0-9]+(\.[0-9]+)?$ ]]; then
  echo "--cover-at expects a non-negative number of seconds, got: $cover_at" >&2
  exit 2
fi
[ -d "$owner" ] || { echo "owner directory not found: $owner" >&2; exit 2; }

mkdir -p "$out"
if [ "$(cd "$owner" && pwd -P)" = "$(cd "$out" && pwd -P)" ]; then
  echo "the output directory must differ from the owner directory" >&2
  exit 2
fi

ffmpeg_quiet=(ffmpeg -y -hide_banner -loglevel error -nostdin)

size_of() { stat -c %s "$1"; }
probe() { ffprobe -v error "$@" | tr -d '\r'; }
note() { echo "NOTE  $1"; }
pass() { echo "PASS  $1  ($2)"; }
fail() { echo "FAIL  $1  ($2)" >&2; exit 1; }

# probe_stream <file> <v|a> <entry>: the first stream of that kind, empty when there is none.
probe_stream() { probe -select_streams "$2:0" -show_entries "stream=$3" -of csv=p=0 "$1" 2>/dev/null | head -n 1; }

# Lines of "file<TAB>type<TAB>role" in the order they go into prepared.json.
records="$(mktemp)"
trap 'rm -f "$records"' EXIT
record() { printf '%s\t%s\t%s\n' "$1" "$2" "$3" >> "$records"; }

mime_of() {
  case "$1" in
    *.png) echo image/png ;;
    *.jpg | *.jpeg) echo image/jpeg ;;
    *.webp) echo image/webp ;;
    *.mp4) echo video/mp4 ;;
    *) fail "$1" "no MIME type for this extension" ;;
  esac
}

# box_before <padded box list> <first> <second>: both boxes are present and the first comes earlier.
box_before() {
  local list="$1" first="$2" second="$3" head_first head_second
  case "$list" in *" $first "*) ;; *) return 1 ;; esac
  case "$list" in *" $second "*) ;; *) return 1 ;; esac
  head_first="${list%% $first *}"; head_second="${list%% $second *}"
  [ "${#head_first}" -lt "${#head_second}" ]
}

lower() { printf '%s' "$1" | tr '[:upper:]' '[:lower:]'; }

# Sort the owner's files into roles by extension.
drawings=(); videos=(); cover_src=""
shopt -s nullglob
for path in "$owner"/*; do
  [ -f "$path" ] || continue
  name="$(basename "$path")"
  lname="$(lower "$name")"
  ext="${lname##*.}"
  case "$lname" in
    cover.webp | cover.jpg | cover.jpeg)
      [ -z "$cover_src" ] || fail "$name" "more than one supplied cover ($(basename "$cover_src") and $name)"
      cover_src="$path"
      continue ;;
  esac
  case "$ext" in
    png | jpg | jpeg | webp | gif | bmp | tif | tiff) drawings+=("$path") ;;
    mp4 | mov | m4v) videos+=("$path") ;;
    *) note "$name is neither a drawing nor a video, skipped" ;;
  esac
done
shopt -u nullglob

if [ "${#videos[@]}" -ne 1 ]; then
  fail "video" "exactly one video is required, found ${#videos[@]} in $owner"
fi
if [ "${#drawings[@]}" -eq 0 ]; then
  echo "WARN  no drawings in $owner, only the video is prepared" >&2
fi

# Drawings.
declare -A taken=()
for path in "${drawings[@]}"; do
  name="$(basename "$path")"
  lname="$(lower "$name")"
  ext="${lname##*.}"
  base="${name%.*}"

  # A still ffmpeg cannot decode is refused here, before anything is written for it.
  if [ -z "$(probe_stream "$path" v codec_name)" ] ||
     ! "${ffmpeg_quiet[@]}" -i "$path" -frames:v 1 -f null - 2>/dev/null; then
    fail "$name" "not an image ffmpeg can read as a still"
  fi

  case "$ext" in
    png | jpg | jpeg | webp) native=1 ;;
    *) native=0 ;;
  esac

  if [ "$native" -eq 1 ] && [ "$(size_of "$path")" -le "$drawing_max" ]; then
    target="$name"
    [ -z "${taken[$target]+x}" ] || fail "$target" "two drawings would be published under this name"
    taken[$target]=1
    cp "$path" "$out/$target"
    pass "$target" "$(size_of "$out/$target") bytes, copied unchanged"
  else
    target="$base.webp"
    [ -z "${taken[$target]+x}" ] || fail "$target" "two drawings would be published under this name"
    taken[$target]=1
    converted=0
    for quality in 90 85 80 75 70 65 60 55 50 45 40; do
      "${ffmpeg_quiet[@]}" -i "$path" -frames:v 1 -c:v libwebp -quality "$quality" "$out/$target"
      if [ "$(size_of "$out/$target")" -le "$drawing_max" ]; then converted=1; break; fi
    done
    [ "$converted" -eq 1 ] || fail "$target" "$(size_of "$out/$target") bytes at quality 40, still above $drawing_max"
    pass "$target" "$(size_of "$out/$target") bytes, converted from $name at quality $quality"
  fi
  record "$target" "$(mime_of "$target")" drawing
done

# Video.
video_src="${videos[0]}"
video_name="$(basename "$video_src")"
video_base="${video_name%.*}"
video_codec="$(probe_stream "$video_src" v codec_name)"
audio_codec="$(probe_stream "$video_src" a codec_name)"
[ "$video_codec" = h264 ] || fail "$video_name" "video codec is ${video_codec:-missing}, expected h264 (H.264)"
if [ -n "$audio_codec" ] && [ "$audio_codec" != aac ]; then
  fail "$video_name" "audio codec is $audio_codec, expected aac"
fi

video_target="$video_base.mp4"
[ -z "${taken[$video_target]+x}" ] || fail "$video_target" "a drawing is already published under this name"
taken[$video_target]=1
video_ext="$(lower "${video_name##*.}")"
list=" $(node "$boxes_tool" "$video_src" | tr '\n' ' ') "
moov_first=0
if box_before "$list" moov mdat; then moov_first=1; fi

if [ "$video_ext" = mp4 ] && [ "$moov_first" -eq 1 ]; then
  cp "$video_src" "$out/$video_target"
  pass "$video_target" "moov before mdat, copied unchanged"
else
  "${ffmpeg_quiet[@]}" -i "$video_src" -map 0:v:0 -map '0:a:0?' -c copy -movflags +faststart "$out/$video_target"
  list=" $(node "$boxes_tool" "$out/$video_target" | tr '\n' ' ') "
  box_before "$list" moov mdat || fail "$video_target" "moov is not before mdat after the remux:$list"
  pass "$video_target" "remuxed with +faststart, no re-encode"
fi
record "$video_target" video/mp4 video

width="$(probe_stream "$out/$video_target" v width)"
height="$(probe_stream "$out/$video_target" v height)"
duration="$(probe -show_entries format=duration -of csv=p=0 "$out/$video_target")"
echo "INFO  $video_target  ${width}x${height}, ${duration} s, $(size_of "$out/$video_target") bytes"

# Cover.
cover_target="$video_base-cover.webp"
[ -z "${taken[$cover_target]+x}" ] || fail "$cover_target" "a drawing is already published under this name"

# long_side_scale <width> <height>: a scale filter that caps the long side without enlarging.
long_side_scale() {
  if [ "$1" -ge "$2" ]; then echo "scale='min($cover_long_side,iw)':-1"; else echo "scale=-1:'min($cover_long_side,ih)'"; fi
}

# encode_cover <input> [-ss args...]: lowers the quality until the WebP is within the cover limit.
encode_cover() {
  local input="$1" seek="$2" src_w src_h filter quality
  src_w="$(probe_stream "$input" v width)"
  src_h="$(probe_stream "$input" v height)"
  filter="$(long_side_scale "$src_w" "$src_h")"
  for quality in 90 85 80 75 70 65 60 55 50 45 40; do
    if [ -n "$seek" ]; then
      "${ffmpeg_quiet[@]}" -ss "$seek" -i "$input" -frames:v 1 -vf "$filter" -c:v libwebp -quality "$quality" "$out/$cover_target"
    else
      "${ffmpeg_quiet[@]}" -i "$input" -frames:v 1 -vf "$filter" -c:v libwebp -quality "$quality" "$out/$cover_target"
    fi
    if [ "$(size_of "$out/$cover_target")" -le "$cover_max" ]; then
      pass "$cover_target" "$(size_of "$out/$cover_target") bytes at quality $quality"
      return
    fi
  done
  fail "$cover_target" "$(size_of "$out/$cover_target") bytes at quality 40, still above $cover_max"
}

if [ -n "$cover_src" ]; then
  [ -z "$cover_at" ] || note "a supplied cover ($(basename "$cover_src")) and --cover-at were both given: the supplied cover wins"
  encode_cover "$cover_src" ""
elif [ -n "$cover_at" ]; then
  encode_cover "$out/$video_target" "$cover_at"
else
  fail "cover" "no cover.webp / cover.jpg in $owner and no --cover-at <seconds> given"
fi
record "$cover_target" image/webp cover

# prepared.json: dimensions and sizes are read back from the files that were written.
export OUT_DIR="$out"
while IFS=$'\t' read -r file type role; do
  w="$(probe_stream "$out/$file" v width)"
  h="$(probe_stream "$out/$file" v height)"
  [ -n "$w" ] && [ -n "$h" ] || fail "$file" "no dimensions"
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$file" "$type" "$(size_of "$out/$file")" "$w" "$h" "$role"
done < "$records" | node -e '
  const fs = require("node:fs");
  const rows = fs.readFileSync(0, "utf8").split("\n").filter((line) => line.length > 0).map((line) => {
    const [file, type, bytes, width, height, role] = line.split("\t");
    return { file, type, bytes: Number(bytes), width: Number(width), height: Number(height), role };
  });
  fs.writeFileSync(process.env.OUT_DIR + "/prepared.json", JSON.stringify(rows, null, 2) + "\n");
  console.log("OK  " + rows.length + " files in " + process.env.OUT_DIR + ", prepared.json written");
'
