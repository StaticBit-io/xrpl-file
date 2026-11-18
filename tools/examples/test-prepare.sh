#!/usr/bin/env bash
# Tests tools/examples/prepare.sh against inputs generated here with ffmpeg, in a temporary folder
# that is removed on exit. Run: bash tools/examples/test-prepare.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
prepare="$here/prepare.sh"
boxes="$here/../fixtures/top-boxes.mjs"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

ffmpeg_quiet=(ffmpeg -y -hide_banner -loglevel error -nostdin)
size_of() { stat -c %s "$1"; }
pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1" >&2; exit 1; }
probe() { ffprobe -v error "$@" | tr -d '\r'; }

[ -f "$prepare" ] || fail "prepare.sh does not exist at $prepare"

# pad_to <file> <bytes>: appends zero bytes after the IEND chunk. PNG decoders stop at IEND and
# ignore what follows, so the file stays a valid PNG of an exact size without a second encoder pass.
pad_to() {
  local file="$1" target="$2" size
  size="$(size_of "$file")"
  [ "$size" -le "$target" ] || fail "fixture $file is $size bytes, above $target"
  head -c "$((target - size))" /dev/zero >> "$file"
}

# box_before <padded box list> <first> <second>: both boxes are present and the first comes earlier.
box_before() {
  local list="$1" first="$2" second="$3" head_first head_second
  case "$list" in *" $first "*) ;; *) return 1 ;; esac
  case "$list" in *" $second "*) ;; *) return 1 ;; esac
  head_first="${list%% $first *}"; head_second="${list%% $second *}"
  [ "${#head_first}" -lt "${#head_second}" ]
}

# json_field <prepared.json> <file> <field>
json_field() {
  node -e '
    const [path, file, field] = process.argv.slice(1);
    const entry = JSON.parse(require("node:fs").readFileSync(path, "utf8")).find((e) => e.file === file);
    if (!entry) process.exit(3);
    process.stdout.write(String(entry[field]));
  ' "$1" "$2" "$3"
}

# Inputs.
owner="$work/owner"; mkdir -p "$owner"
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=640x480 -frames:v 1 "$owner/fits.png"
cp "$owner/fits.png" "$owner/exact.png"; pad_to "$owner/exact.png" 1000000
cp "$owner/fits.png" "$owner/over.png"; pad_to "$owner/over.png" 1000001
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=320x240 -frames:v 1 -q:v 3 "$owner/photo.jpg"
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=320x240 -frames:v 1 "$owner/plain.gif"

# moov at the end: the mp4 muxer's default without +faststart.
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine=frequency=440 -t 3 \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest "$owner/clip.mp4"
list=" $(node "$boxes" "$owner/clip.mp4" | tr '\n' ' ') "
box_before "$list" mdat moov || fail "fixture clip.mp4 does not have moov last: $list"

# Case 1: drawings and a moov-last video, with a frame cover.
out="$work/out1"
bash "$prepare" --cover-at 1 "$owner" "$out" > "$work/log1" 2>&1 || { cat "$work/log1"; fail "prepare.sh failed on valid input"; }

cmp -s "$owner/fits.png" "$out/fits.png" || fail "fits.png is not byte for byte"
pass "a PNG that fits is copied byte for byte"
cmp -s "$owner/exact.png" "$out/exact.png" || fail "exact.png (1,000,000 bytes) is not byte for byte"
pass "a PNG of exactly 1,000,000 bytes is copied byte for byte"
cmp -s "$owner/photo.jpg" "$out/photo.jpg" || fail "photo.jpg is not byte for byte"
pass "a JPEG that fits is copied byte for byte"

[ -f "$out/over.webp" ] && [ ! -f "$out/over.png" ] || fail "over.png (1,000,001 bytes) was not converted to over.webp"
[ "$(size_of "$out/over.webp")" -le 1000000 ] || fail "over.webp is above 1,000,000 bytes"
[ "$(probe -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$out/over.webp")" = webp ] || fail "over.webp is not WebP"
pass "a PNG of 1,000,001 bytes becomes a WebP of at most 1,000,000"

[ -f "$out/plain.webp" ] || fail "plain.gif was not converted to plain.webp"
pass "a GIF becomes a WebP"

[ "$(probe -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$out/clip.mp4")" = h264 ] || fail "clip.mp4 codec changed"
list=" $(node "$boxes" "$out/clip.mp4" | tr '\n' ' ') "
box_before "$list" moov mdat || fail "clip.mp4: moov is not before mdat: $list"
pass "a moov-last MP4 is remuxed, moov before mdat"
count_in="$(probe -count_packets -select_streams v:0 -show_entries stream=nb_read_packets -of csv=p=0 "$owner/clip.mp4")"
count_out="$(probe -count_packets -select_streams v:0 -show_entries stream=nb_read_packets -of csv=p=0 "$out/clip.mp4")"
[ "$count_in" = "$count_out" ] || fail "video packets $count_in -> $count_out: the remux re-encoded"
pass "the remux did not re-encode ($count_in video packets both sides)"

[ -f "$out/clip-cover.webp" ] || fail "no clip-cover.webp for --cover-at 1"
[ "$(size_of "$out/clip-cover.webp")" -le 65536 ] || fail "clip-cover.webp is above 65,536 bytes"
width="$(probe -select_streams v:0 -show_entries stream=width -of csv=p=0 "$out/clip-cover.webp")"
height="$(probe -select_streams v:0 -show_entries stream=height -of csv=p=0 "$out/clip-cover.webp")"
long=$(( width > height ? width : height ))
{ [ "$long" -le 960 ] && [ "$long" -gt 0 ]; } || fail "clip-cover.webp is ${width}x${height}, long side above 960"
pass "--cover-at 1 makes a cover of ${width}x${height}, at most 65,536 bytes"

prepared="$out/prepared.json"
[ "$(json_field "$prepared" clip.mp4 role)" = video ] || fail "prepared.json: clip.mp4 role"
[ "$(json_field "$prepared" clip.mp4 type)" = video/mp4 ] || fail "prepared.json: clip.mp4 type"
[ "$(json_field "$prepared" clip.mp4 width)" = 1280 ] || fail "prepared.json: clip.mp4 width"
[ "$(json_field "$prepared" clip.mp4 height)" = 720 ] || fail "prepared.json: clip.mp4 height"
[ "$(json_field "$prepared" clip.mp4 bytes)" = "$(size_of "$out/clip.mp4")" ] || fail "prepared.json: clip.mp4 bytes"
[ "$(json_field "$prepared" clip-cover.webp role)" = cover ] || fail "prepared.json: cover role"
[ "$(json_field "$prepared" clip-cover.webp type)" = image/webp ] || fail "prepared.json: cover type"
[ "$(json_field "$prepared" exact.png role)" = drawing ] || fail "prepared.json: exact.png role"
[ "$(json_field "$prepared" exact.png type)" = image/png ] || fail "prepared.json: exact.png type"
[ "$(json_field "$prepared" exact.png bytes)" = 1000000 ] || fail "prepared.json: exact.png bytes"
[ "$(json_field "$prepared" exact.png width)" = 640 ] || fail "prepared.json: exact.png width"
[ "$(json_field "$prepared" photo.jpg type)" = image/jpeg ] || fail "prepared.json: photo.jpg type"
[ "$(json_field "$prepared" over.webp type)" = image/webp ] || fail "prepared.json: over.webp type"
pass "prepared.json lists every file with role, type, bytes and dimensions"

# Case 2: a supplied cover wins over --cover-at, and the script says so.
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=640x360 -frames:v 1 "$owner/cover.jpg"
out="$work/out2"
bash "$prepare" --cover-at 1 "$owner" "$out" > "$work/log2" 2>&1 || { cat "$work/log2"; fail "prepare.sh failed with a supplied cover"; }
grep -q "supplied cover" "$work/log2" || fail "no message that the supplied cover wins"
width="$(probe -select_streams v:0 -show_entries stream=width -of csv=p=0 "$out/clip-cover.webp")"
[ "$width" = 640 ] || fail "the cover is ${width} px wide, expected the supplied 640 (a frame would be 960)"
[ ! -f "$out/cover.jpg" ] || fail "cover.jpg was copied as a drawing"
[ "$(size_of "$out/clip-cover.webp")" -le 65536 ] || fail "supplied cover above 65,536 bytes"
pass "a supplied cover.jpg is used instead of a frame, and the script says so"
rm "$owner/cover.jpg"

# Case 3: HEVC is refused, and the message names the codec.
hevc="$work/owner-hevc"; mkdir -p "$hevc"
"${ffmpeg_quiet[@]}" -f lavfi -i testsrc2=size=320x240:rate=25 -f lavfi -i sine=frequency=440 -t 1 \
  -c:v libx265 -tag:v hvc1 -c:a aac -shortest -movflags +faststart "$hevc/clip.mp4"
if bash "$prepare" --cover-at 0 "$hevc" "$work/out3" > "$work/log3" 2>&1; then fail "HEVC input was accepted"; fi
grep -qi hevc "$work/log3" || fail "the HEVC failure does not name the codec: $(cat "$work/log3")"
pass "an HEVC video fails, naming the codec"

# Case 4: two videos fail.
two="$work/owner-two"; mkdir -p "$two"
cp "$owner/clip.mp4" "$two/a.mp4"; cp "$owner/clip.mp4" "$two/b.mp4"
if bash "$prepare" --cover-at 0 "$two" "$work/out4" > "$work/log4" 2>&1; then fail "two videos were accepted"; fi
grep -qi "exactly one video" "$work/log4" || fail "the two-videos failure is not clear: $(cat "$work/log4")"
pass "two videos fail with a clear message"

echo "OK  all prepare.sh checks passed"
