# Synthetic regression fixtures

`谷歌自己合成的.heic` is a deliberately synthetic, non-device regression fixture.
It is not part of the formal device corpus and must never be presented as a
Google, Apple, or other vendor sample.

- Provenance: existing local malformed-XMP regression fixture, copied unchanged
  into this versioned test-fixture location for P4 R5.
- Intended coverage: fail-closed handling when live-photo XMP properties and
  `Container:Directory` do not share one `rdf:Description` owner.
- SHA-256: `B75BD5CAD6E8036929E50C9617982C857C9D2D072686D4C156FAA9BAC82F47F6`
- Byte length: `6293579`

Tests verify this hash so fixture drift cannot silently alter the malformed
input they exercise.

## Short video timing fixtures (2026-11-03)

These are generated test patterns, not camera or device evidence. Test execution
uses the fixed files and the product Native encoder; it does not require FFmpeg.

| File | Bytes | SHA-256 | Independently checked schedule |
|---|---:|---|---|
| `vfr-silent-last-short.mp4` | 11290 | `B267AC6156E653BB268FC06E205F4271AD5A16B2732A0D00BAA492921A9BDFF6` | 8 frames at 0,.1,.2,.3,.6,.9,1.2,1.5 seconds; final duration .1 seconds; total 1.6 seconds |
| `bframes-silent-cfr.mp4` | 14290 | `F00813467BE53AF70E1B9A95EBE5FA298D23FAF8993CD1C1FBCC81042B7BEBAA` | 16 frames at 10fps; B frames exercise decoder drain; total 1.6 seconds |

Both use 128×96 `testsrc2`, AVC/yuv420p, explicit BT.709 CICP and colr, and no audio.
Offline generation used FFmpeg 9.0.2/libx264. VFR selected `lt(n,4)+not(mod(n,3))`
from a 10fps/1.6s input, with `-fps_mode vfr -bf 0 -use_editlist 0`; the B-frame
fixture used `-bf 2` and the normal complete-frame edit. Both set
`-x264-params colorprim=bt709:transfer=bt709:colormatrix=bt709 -movflags +write_colr`.
