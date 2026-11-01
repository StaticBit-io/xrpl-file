#!/usr/bin/env bash
# Builds the fixture files the testnet publication carries, from the owner's two source files.
#
#   tools/fixtures/make-media.sh <source-dir> <platform remux.mjs> <out-dir>
#
# <source-dir> holds en-back.mp4 (H.264/AAC, moov first) and picture.webp. <platform remux.mjs> is
# the video platform's js/remux.mjs, imported by fragment.mjs for its assembleFragmented. Each file
# is checked as it is made, and any failed check stops the script: a fixture that does not have the
# property its name promises would make a later test pass or fail for the wrong reason.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: make-media.sh <source-dir> <platform remux.mjs> <out-dir>" >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
src="$1"
remux="$2"
out="$3"

mkdir -p "$out"
ffmpeg_quiet=(ffmpeg -y -hide_banner -loglevel error -nostdin)

size_of() { stat -c %s "$1"; }
boxes() { node "$here/top-boxes.mjs" "$1"; }
pass() { echo "PASS  $1  ($2)"; }
fail() { echo "FAIL  $1  ($2)" >&2; exit 1; }

# check_size <file> <min exclusive|-> <max inclusive|->
check_size() {
  local file="$1" min="$2" max="$3" size
  size="$(size_of "$out/$file")"
  if [ "$min" != "-" ] && [ "$size" -le "$min" ]; then fail "$file" "$size bytes, expected more than $min"; fi
  if [ "$max" != "-" ] && [ "$size" -gt "$max" ]; then fail "$file" "$size bytes, expected at most $max"; fi
  pass "$file" "$size bytes"
}

# check_order <file> <first box> <second box>: the first appears before the second among the top-level boxes.
check_order() {
  local file="$1" first="$2" second="$3" list
  list=" $(boxes "$out/$file") "
  local before="${list%% $first *}" after_second="${list%% $second *}"
  case "$list" in *" $first "*) ;; *) fail "$file" "no $first box in:$list" ;; esac
  case "$list" in *" $second "*) ;; *) fail "$file" "no $second box in:$list" ;; esac
  if [ "${#before}" -ge "${#after_second}" ]; then fail "$file" "$first is not before $second in:$list"; fi
  pass "$file" "$first before $second"
}

# picture.webp: the owner's picture, as it is.
cp "$src/picture.webp" "$out/picture.webp"
pass picture.webp "$(size_of "$out/picture.webp") bytes, copied"

# picture-blocks.png: stored uncompressed, so the file spans more than one 1,024,000-byte Merkle block.
"${ffmpeg_quiet[@]}" -i "$src/picture.webp" -compression_level 0 "$out/picture-blocks.png"
check_size picture-blocks.png 1024000 -

# Covers: a 16:9 crop of the picture, quality lowered until the file fits the 65,536-byte cover limit.
# cover-b is the mirror image, so the two have different bytes and so different manifests.
make_cover() {
  local name="$1" filter="$2" quality
  for quality in 85 80 75 70 65 60 55 50 45 40; do
    "${ffmpeg_quiet[@]}" -i "$src/picture.webp" -vf "$filter" -frames:v 1 -c:v libwebp -quality "$quality" "$out/$name"
    if [ "$(size_of "$out/$name")" -le 65536 ]; then
      check_size "$name" - 65536
      return
    fi
  done
  fail "$name" "$(size_of "$out/$name") bytes at quality 40, still above 65536"
}
make_cover cover.webp "crop=1600:900,scale=960:540"
make_cover cover-b.webp "crop=1600:900,scale=960:540,hflip"

# text-cyrillic.txt: multi-byte text, and markup that must reach the screen as text.
cat > "$out/text-cyrillic.txt" <<'TEXT'
Реестр — это не база данных, а журнал.
The ledger is a log, not a database.
Каждый блок файла проверяется по отпечатку, записанному в манифесте.
Every block of the file is checked against the fingerprint in the manifest.
Строка с кириллицей: съешь же ещё этих мягких французских булок, да выпей чаю.
A line with accents: déjà vu, naïve café, Zoë, crème brûlée.
Тире — длинное, кавычки «ёлочки» и „лапки“, многоточие…
Dash — long, quotes “curly” and ‘single’, ellipsis…
Эта строка содержит разметку: <b>not bold</b>, и она должна остаться текстом.
Markup: <b>not bold</b> <i>not italic</i> <a href="https://example.com">not a link</a>
Амперсанд & меньше < больше > кавычка " апостроф '
Ampersand & less < greater > quote " apostrophe '
Числа: 1 2 3 4 5 6 7 8 9 10, дроби 1/2 и 3/4, степень x² и y³.
Tabs:	one	two	three
Табуляция:	раз	два	три
Пустая строка ниже.

Строка после пустой.
Line after the blank one.
Длинная строка: Lorem ipsum по-русски — мы пишем это, чтобы строка не помещалась в узкий экран и переносилась по словам, а не обрезалась.
A long line: the quick brown fox jumps over the lazy dog, and then does it again so that this line has to wrap in a narrow viewer rather than be cut off.
Эмодзи: ✓ ✗ ★ ☆ → ← ↑ ↓
Symbols: © ® ™ § ¶ † ‡ • ‰ € £ ¥
Греческие буквы: α β γ δ ε ζ η θ — они тоже не ASCII.
Greek letters: alpha beta gamma delta epsilon zeta eta theta.
Конец первой половины.
End of the first half.
Вторая половина начинается здесь.
The second half begins here.
Ещё одна строка с <script>alert('text')</script> — это просто текст.
One more line with <script>alert('text')</script> — it is plain text.
Строки с номерами: 28, 29, 30, 31.
Numbered lines: 32, 33, 34, 35.
Последняя строка без точки в конце
The last line has no full stop at the end
TEXT
pass text-cyrillic.txt "$(wc -l < "$out/text-cyrillic.txt") lines, $(size_of "$out/text-cyrillic.txt") bytes"

# text-large.txt: past the 1,048,576-byte mark the text view truncates at. LC_ALL=C makes awk count bytes.
LC_ALL=C awk 'BEGIN { n = 0; total = 0
  while (total < 1100000) { n++; line = "line " n " — строка " n "\n"; printf "%s", line; total += length(line) } }' \
  > "$out/text-large.txt"
check_size text-large.txt 1048576 -

# page.html and drawing.svg: active content the element must show inertly, never run.
cat > "$out/page.html" <<'HTML'
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>xrpl-file page fixture</title></head>
<body>
<h1>A page that tries to run</h1>
<p>If this page is ever executed, an alert appears.</p>
<script>alert('xrpl-file')</script>
<img src=x onerror="alert(2)">
</body>
</html>
HTML
pass page.html "$(size_of "$out/page.html") bytes"

cat > "$out/drawing.svg" <<'SVG'
<svg xmlns="http://www.w3.org/2000/svg" width="200" height="120" viewBox="0 0 200 120" onload="alert(1)">
  <script>alert('svg')</script>
  <rect x="10" y="10" width="180" height="100" rx="12" fill="#4da3ff"/>
  <circle cx="100" cy="60" r="30" fill="#14161a"/>
</svg>
SVG
pass drawing.svg "$(size_of "$out/drawing.svg") bytes"

# MP4 layouts. The source has its moov first; ffmpeg without +faststart writes it last.
cp "$src/en-back.mp4" "$out/plain-start.mp4"
check_order plain-start.mp4 moov mdat

"${ffmpeg_quiet[@]}" -i "$src/en-back.mp4" -map 0 -c copy "$out/plain-end.mp4"
check_order plain-end.mp4 mdat moov

node "$here/fragment.mjs" "$remux" "$out/plain-start.mp4" "$out/fragmented.mp4"
list="$(boxes "$out/fragmented.mp4")"
case "$list" in
  "ftyp moov sidx moof"*) pass fragmented.mp4 "$(size_of "$out/fragmented.mp4") bytes, ${list:0:40}..." ;;
  *) fail fragmented.mp4 "expected ftyp moov sidx moof ..., got ${list:0:60}" ;;
esac

# The Opus encoder starts its first packet at -0.007 s (pre-skip), and Chrome's Media Source parser rejects such a
# WebM ("a block with a timecode before the previous block"). -avoid_negative_ts make_zero does not shift a WebM;
# taking the audio from a second input delayed by a little more than the pre-skip does, and leaves the video at 0.
"${ffmpeg_quiet[@]}" -i "$src/en-back.mp4" -itsoffset 0.007 -i "$src/en-back.mp4" -map 0:v:0 -map 1:a:0   -c:v libvpx-vp9 -b:v 1M -g 60 -row-mt 1 -c:a libopus -b:a 96k "$out/clip.webm"
negative="$(ffprobe -v error -show_entries packet=pts_time -of csv=p=0 "$out/clip.webm" | awk -F, '$1 < 0' | wc -l)"
[ "$negative" -eq 0 ] || fail clip.webm "$negative packets start before 0, which Chrome's MSE refuses"
check_size clip.webm 0 -

"${ffmpeg_quiet[@]}" -i "$src/en-back.mp4" -c:v libx265 -crf 28 -tag:v hvc1 -c:a copy -movflags +faststart "$out/hevc.mp4"
check_size hevc.mp4 0 -

# large-end.mp4: 33 copies of the source end to end, for the file that does not fit in memory.
"${ffmpeg_quiet[@]}" -stream_loop 32 -i "$src/en-back.mp4" -c copy "$out/large-end.mp4"
check_size large-end.mp4 249999999 400000000
check_order large-end.mp4 mdat moov

count="$(ls "$out" | wc -l)"
if [ "$count" -ne 14 ]; then fail "output" "$count files in $out, expected 14"; fi
echo "OK  $count files in $out"
