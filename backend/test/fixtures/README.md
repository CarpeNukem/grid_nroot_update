# Media fixtures

`media-real.test.ts` checks the MP4 inspector against genuinely encoded files —
a real container is full of `mdat`, `free`, and `udta` boxes that a hand-built
fixture does not have, so this is what proves the box walker works on files
people will actually upload.

The binaries are **not committed**. They are derived from whatever source video
was used to produce them, and this repository is public; committing them would
publish that content permanently. The tests skip when they are absent, so a
fresh clone still runs green. The synthetic MP4 tests in `media.test.ts` cover
the parser either way.

## Regenerating them

Two files are needed, both remuxed from one source so they differ **only** by
the presence of an audio track — that difference is the thing under test:

```bash
ffmpeg -i source.mp4 -t 1 -c copy withaudio.mp4
ffmpeg -i source.mp4 -t 1 -c copy -an silent.mp4
```

Any short video with an audio track works. `-c copy` avoids re-encoding, so it
runs on an ffmpeg build without encoders.

If the source has different dimensions, update the expected `width`/`height` in
`media-real.test.ts` to match:

```bash
ffprobe -v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 silent.mp4
```
