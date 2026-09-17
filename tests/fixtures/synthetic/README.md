# Synthetic HEIC regression fixture

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
