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
# else becomes WebP. The video must be H.264/AAC in a pixel format and profile browsers decode
# (8-bit 4:2:0, Constrained Baseline, Baseline, Main or High); it is remuxed with +faststart, never
# re-encoded, when its moov box is not before mdat or it carries more than one video and one audio
# stream. Each failed check stops the script. Extensions are matched without regard to case.
#
# Metadata is never stripped (the bytes are the owner's). Every prepared file is scanned instead, and
# a WARN line names what it carries (EXIF/GPS/XMP/text chunks in a still, global and stream tags such
# as location or creation_time in the video), because the files are published permanently.
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
rm -f "$out/prepared.json"
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

# mime_of <file>: call it as an assignment (mime="$(mime_of "$f")"), so that its `fail` stops the script:
# `exit` inside a command substitution leaves only the substitution, and errexit ignores it as an argument.
mime_of() {
  case "$(lower "$1")" in
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

# Drawings. Names are claimed in lower case: two files that differ only in case would overwrite
# each other on a case-insensitive file system.
declare -A taken=()
# claim <target> <message>: refuses a name another published file already has.
claim() {
  local slot
  slot="$(lower "$1")"
  [ -z "${taken[$slot]+x}" ] || fail "$1" "$2"
  taken[$slot]=1
}
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

  # The extension decides the MIME type that is published, so the content has to be what it claims.
  codec="$(probe_stream "$path" v codec_name)"
  case "$codec" in
    png | mjpeg | webp | gif | bmp | tiff) ;;
    *) fail "$name" "contains $codec, which is not a still image codec" ;;
  esac
  case "$ext" in
    png) native=1; expected=png ;;
    jpg | jpeg) native=1; expected=mjpeg ;;
    webp) native=1; expected=webp ;;
    gif) native=0; expected=gif ;;
    bmp) native=0; expected=bmp ;;
    *) native=0; expected=tiff ;;
  esac
  [ "$codec" = "$expected" ] || fail "$name" "extension says $expected but the content is $codec"

  if [ "$native" -eq 1 ] && [ "$(size_of "$path")" -le "$drawing_max" ]; then
    target="$name"
    claim "$target" "two drawings would be published under this name"
    cp "$path" "$out/$target"
    pass "$target" "$(size_of "$out/$target") bytes, copied unchanged"
  else
    target="$base.webp"
    claim "$target" "two drawings would be published under this name"
    converted=0
    for quality in 90 85 80 75 70 65 60 55 50 45 40; do
      rm -f "$out/$target"
      "${ffmpeg_quiet[@]}" -i "$path" -frames:v 1 -c:v libwebp -quality "$quality" "$out/$target"
      [ -s "$out/$target" ] || fail "$target" "ffmpeg wrote no file for $name"
      if [ "$(size_of "$out/$target")" -le "$drawing_max" ]; then converted=1; break; fi
    done
    [ "$converted" -eq 1 ] || fail "$target" "$(size_of "$out/$target") bytes at quality 40, still above $drawing_max"
    pass "$target" "$(size_of "$out/$target") bytes, converted from $name at quality $quality"
  fi
  mime="$(mime_of "$target")"
  record "$target" "$mime" drawing
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
# H.264 is not enough: High 4:4:4, 4:2:2 and 10-bit streams are H.264 too and no mainstream browser plays them.
pix_fmt="$(probe_stream "$video_src" v pix_fmt)"
profile="$(probe_stream "$video_src" v profile)"
case "$pix_fmt" in
  yuv420p | yuvj420p) ;;
  *) fail "$video_name" "pixel format is ${pix_fmt:-unknown} (profile ${profile:-unknown}); browsers decode only 8-bit 4:2:0 (yuv420p)" ;;
esac
case "$profile" in
  "Constrained Baseline" | Baseline | Main | High) ;;
  *) fail "$video_name" "H.264 profile is ${profile:-unknown}; browsers decode Constrained Baseline, Baseline, Main and High" ;;
esac
stream_count="$(probe -show_entries stream=index -of csv=p=0 "$video_src" | grep -c .)"

video_target="$video_base.mp4"
claim "$video_target" "a drawing is already published under this name"
video_ext="$(lower "${video_name##*.}")"
list=" $(node "$boxes_tool" "$video_src" | tr '\n' ' ') "
moov_first=0
if box_before "$list" moov mdat; then moov_first=1; fi

# Extra streams (timecode, data, subtitles) would be published with the file, so only a file that is exactly
# one video and at most one audio stream is copied as it is.
expected_streams=1
[ -z "$audio_codec" ] || expected_streams=2
if [ "$video_ext" = mp4 ] && [ "$moov_first" -eq 1 ] && [ "$stream_count" -eq "$expected_streams" ]; then
  cp "$video_src" "$out/$video_target"
  pass "$video_target" "moov before mdat, copied unchanged"
else
  # bitexact keeps ffmpeg from adding its own encoder tag to the owner's file.
  "${ffmpeg_quiet[@]}" -i "$video_src" -map 0:v:0 -map '0:a:0?' -c copy -fflags +bitexact -movflags +faststart "$out/$video_target"
  list=" $(node "$boxes_tool" "$out/$video_target" | tr '\n' ' ') "
  box_before "$list" moov mdat || fail "$video_target" "moov is not before mdat after the remux:$list"
  pass "$video_target" "remuxed with +faststart (video and first audio stream only), no re-encode"
fi
record "$video_target" video/mp4 video

width="$(probe_stream "$out/$video_target" v width)"
height="$(probe_stream "$out/$video_target" v height)"
duration="$(probe -show_entries format=duration -of csv=p=0 "$out/$video_target")"
echo "INFO  $video_target  ${width}x${height}, ${duration} s, $(size_of "$out/$video_target") bytes"

# Cover.
cover_target="$video_base-cover.webp"
claim "$cover_target" "a drawing is already published under this name"

# long_side_scale <width> <height>: a scale filter that caps the long side without enlarging.
long_side_scale() {
  if [ "$1" -ge "$2" ]; then echo "scale='min($cover_long_side,iw)':-1"; else echo "scale=-1:'min($cover_long_side,ih)'"; fi
}

# encode_cover <input> [-ss args...]: lowers the quality until the WebP is within the cover limit.
encode_cover() {
  local input="$1" seek="$2" src_w src_h filter quality out_w out_h
  src_w="$(probe_stream "$input" v width)"
  src_h="$(probe_stream "$input" v height)"
  filter="$(long_side_scale "$src_w" "$src_h")"
  for quality in 90 85 80 75 70 65 60 55 50 45 40; do
    rm -f "$out/$cover_target"
    if [ -n "$seek" ]; then
      "${ffmpeg_quiet[@]}" -ss "$seek" -i "$input" -frames:v 1 -vf "$filter" -c:v libwebp -quality "$quality" "$out/$cover_target"
    else
      "${ffmpeg_quiet[@]}" -i "$input" -frames:v 1 -vf "$filter" -c:v libwebp -quality "$quality" "$out/$cover_target"
    fi
    [ -s "$out/$cover_target" ] || fail "$cover_target" "ffmpeg wrote no cover (is the frame inside the video?)"
    if [ "$(size_of "$out/$cover_target")" -le "$cover_max" ]; then
      out_w="$(probe_stream "$out/$cover_target" v width)"
      out_h="$(probe_stream "$out/$cover_target" v height)"
      { [ -n "$out_w" ] && [ -n "$out_h" ]; } || fail "$cover_target" "the written cover has no readable dimensions"
      [ "$(( out_w > out_h ? out_w : out_h ))" -le "$cover_long_side" ] || fail "$cover_target" "long side is above $cover_long_side"
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
  if awk -v at="$cover_at" -v len="$duration" 'BEGIN { exit !(at >= len) }'; then
    fail "cover" "--cover-at $cover_at is at or past the end of the video, which lasts $duration s"
  fi
  encode_cover "$out/$video_target" "$cover_at"
else
  fail "cover" "no cover.webp / cover.jpg in $owner and no --cover-at <seconds> given"
fi
record "$cover_target" image/webp cover

# Metadata warnings. Each prepared file is published byte for byte and permanently, so what it carries
# is named here; the owner may strip it from the source and run again.
warn_metadata() {
  echo "WARN  $1 carries metadata: $2. It will be published permanently, byte for byte; strip it from the source and run this again if you do not want that." >&2
}

# Container tags that say nothing about the owner: brand, language and the track handler's name.
benign_tags=" major_brand minor_version compatible_brands language handler_name vendor_id "
while IFS=$'\t' read -r file type role; do
  case "$type" in
    video/mp4)
      tags=""
      while IFS= read -r line; do
        key="${line#TAG:}"; key="$(lower "${key%%=*}")"
        case "$benign_tags" in *" $key "*) continue ;; esac
        case " $tags " in *" $key "*) ;; *) tags="${tags:+$tags, }$key" ;; esac
      done < <(probe -show_entries format_tags:stream_tags -of default=nw=1:nk=0 "$out/$file" | grep '^TAG:' || true)
      [ -z "$tags" ] || warn_metadata "$file" "$tags"
      ;;
    *)
      found="$(node "$here/scan-metadata.mjs" "$out/$file")"
      [ -z "$found" ] || warn_metadata "$file" "$found"
      ;;
  esac
done < "$records"

# prepared.json: dimensions and sizes are read back from the files that were written. Every row is
# collected before the manifest is written, so a failed run leaves no partial prepared.json behind.
rows="$(mktemp)"
trap 'rm -f "$records" "$rows"' EXIT
while IFS=$'\t' read -r file type role; do
  w="$(probe_stream "$out/$file" v width)"
  h="$(probe_stream "$out/$file" v height)"
  { [ -n "$w" ] && [ -n "$h" ]; } || fail "$file" "no dimensions"
  printf '%s\t%s\t%s\t%s\t%s\t%s\n' "$file" "$type" "$(size_of "$out/$file")" "$w" "$h" "$role" >> "$rows"
done < "$records"
OUT_DIR="$out" ROWS_FILE="$rows" node -e '
  const fs = require("node:fs");
  const rows = fs.readFileSync(process.env.ROWS_FILE, "utf8").split(/\r?\n/).filter((line) => line.length > 0).map((line) => {
    const [file, type, bytes, width, height, role] = line.split(/\t/);
    return { file, type, bytes: Number(bytes), width: Number(width), height: Number(height), role };
  });
  fs.writeFileSync(process.env.OUT_DIR + "/prepared.json", JSON.stringify(rows, null, 2) + "\n");
  console.log("OK  " + rows.length + " files in " + process.env.OUT_DIR + ", prepared.json written");
'
