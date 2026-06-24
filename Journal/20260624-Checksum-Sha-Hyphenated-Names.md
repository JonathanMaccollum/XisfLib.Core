# 2026-06-24 — Accept spec-hyphenated checksum algorithm names

## What

`XmlSerializer.ParseChecksum` only recognized the non-hyphenated `sha1`/`sha256`/`sha512`
while the XISF 1.0 spec (§10.5, Table 9) — and PixInsight / N.I.N.A. — write the hyphenated
`sha-1`/`sha-256`/`sha-512`. Any image whose data block carried `checksum="sha-1:…"` threw
`FormatException: Unsupported hash algorithm: sha-1` out of `DeserializeImage`, so the whole
metadata read failed.

Fixed `ParseChecksum` to accept both the spec and legacy forms, and changed `FormatChecksum`
to *emit* the spec-compliant hyphenated names so files this library writes round-trip through
conformant readers. Version 1.1.1 → 1.1.2 (`XisfLib.Core.csproj`).

Note: `ChecksumProvider.ParseAlgorithm` already accepted `sha-1`, but it is not on the
deserialization path — `XmlSerializer` has its own parser, which was the one that threw.

## Why

Diagnosed from two N.I.N.A. 3.2 light frames the EigenFrame desktop reported as
"could not be read as a valid XISF image" despite being byte-valid (data-block SHA-1 matched
the embedded checksum). The header read is metadata-only and never touches the data block, so
the failure was purely the checksum-attribute parse. Repro: feeding the header bytes to
`XisfReader.ReadMetadataFromStream(_, ".xisf")` against 1.1.1 throws; against the fixed build
it returns the image.

`XmlSerializer.cs` — `ParseChecksum` (~736), `FormatChecksum` (~660).
