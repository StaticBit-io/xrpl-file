#!/usr/bin/env bash
# Regenerates the committed test media and expected.json next to this script.
#
#   bash web/test/media/make.sh <path to the video platform's js/remux.mjs>
#
# remux.mjs is only imported by tools/fixtures/fragment.mjs, for fragmented-sidx.mp4: the platform's
# own layout (merged moov, sidx, fragments interleaved by time) is what the element has to read.
#
# 160x90 at 10 fps, 4 s, a keyframe every 10 frames. x264's defaults put B-frames in the H.264
# media, so decode and composition times differ, as in the owner's en-back.mp4.
set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: make.sh <platform remux.mjs>" >&2
  exit 2
fi

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
remux="$1"

# bitexact keeps timestamps and the encoder tag out of the muxer output, so a rerun gives the same bytes.
ff=(ffmpeg -y -hide_banner -loglevel error -nostdin -fflags +bitexact)
inputs=(-f lavfi -i "testsrc2=size=160x90:rate=10" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 4)
avc=(-c:v libx264 -g 10 -keyint_min 10 -sc_threshold 0 -pix_fmt yuv420p -flags:v +bitexact -c:a aac -b:a 32k -flags:a +bitexact)

"${ff[@]}" "${inputs[@]}" "${avc[@]}" -movflags +faststart "$here/plain-start.mp4"
"${ff[@]}" "${inputs[@]}" "${avc[@]}" "$here/plain-end.mp4"
# A QuickTime file with a tmcd timecode track: one sample, near the start of the media data, that a seek must not be pulled to.
"${ff[@]}" "${inputs[@]}" "${avc[@]}" -timecode 00:00:00:00 -f mov "$here/timecode.mov"
"${ff[@]}" "${inputs[@]}" "${avc[@]}" -movflags +frag_keyframe+empty_moov+default_base_moof "$here/fragmented.mp4"

"${ff[@]}" "${inputs[@]}" -c:v libx265 -tag:v hvc1 -g 10 -keyint_min 10 -pix_fmt yuv420p -x265-params log-level=error \
  -c:a aac -b:a 32k "$here/hevc.mp4"

# ffmpeg's Opus encoder starts its first packet at -0.007 s (pre-skip), and Chrome's Media Source parser
# rejects such a WebM (CHUNK_DEMUXER_ERROR_APPEND_FAILED, "a block with a timecode before the previous
# block"). -avoid_negative_ts make_zero does not shift a WebM; delaying the audio input by a little more
# than the pre-skip does, and leaves the video at 0.
webm=(-c:v libvpx-vp9 -g 10 -b:v 100k -pix_fmt yuv420p -c:a libopus -b:a 32k)
webm_inputs=(-f lavfi -i "testsrc2=size=160x90:rate=10" -itsoffset 0.007 -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 4)
"${ff[@]}" "${webm_inputs[@]}" "${webm[@]}" "$here/clip.webm"
# Written to a pipe the muxer cannot seek back in: no Cues, and the Segment and Clusters keep unknown sizes.
"${ff[@]}" "${webm_inputs[@]}" "${webm[@]}" -f webm pipe:1 > "$here/clip-live.webm"

# The muxer as most files meet it: no delay on the audio input. Opus blocks are stored shifted back by the
# codec delay, so inside a Cluster an audio block (101 ms) stands ahead of a video block (100 ms) that an
# earlier packet time would put first. Media Source refuses such a Cluster; the feed re-orders it. The
# tests read the raw Clusters of this file to prove the inversion is really there.
inverted_inputs=(-f lavfi -i "testsrc2=size=160x90:rate=30" -f lavfi -i "sine=frequency=440:sample_rate=48000" -t 2)
"${ff[@]}" "${inverted_inputs[@]}" -c:v libvpx-vp9 -g 30 -b:v 100k -pix_fmt yuv420p -c:a libopus -b:a 32k "$here/clip-inverted.webm"

node "$root/tools/fixtures/fragment.mjs" "$remux" "$here/plain-start.mp4" "$here/fragmented-sidx.mp4"

# File offsets of the video keyframes, which the movie tests compare the parsed sample tables against.
keyframes() {
  ffprobe -v error -select_streams v -show_packets -of json "$1" | node -e '
    let text = "";
    process.stdin.on("data", (chunk) => { text += chunk; });
    process.stdin.on("end", () => {
      const packets = JSON.parse(text).packets;
      const offsets = packets.filter((p) => p.flags.includes("K")).map((p) => Number(p.pos));
      process.stdout.write(JSON.stringify({ videoKeyframeOffsets: offsets }));
    });'
}
{
  printf '{\n  "plain-start.mp4": %s,\n  "plain-end.mp4": %s\n}\n' \
    "$(keyframes "$here/plain-start.mp4")" "$(keyframes "$here/plain-end.mp4")"
} > "$here/expected.json"

for f in plain-start.mp4 plain-end.mp4 timecode.mov fragmented.mp4 fragmented-sidx.mp4 hevc.mp4 clip.webm clip-live.webm clip-inverted.webm expected.json; do
  size="$(stat -c %s "$here/$f")"
  [ "$size" -lt 200000 ] || { echo "$f is $size bytes, over the 200 KB budget" >&2; exit 1; }
  echo "$f  $size bytes"
done
