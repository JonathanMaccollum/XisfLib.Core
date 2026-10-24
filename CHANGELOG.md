# Changelog

Notable changes per minor version (patch numbers are CI run numbers and carry no meaning).
Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

## 1.3

XISF 1.0 **Revision 1** conformance release (spec document v1.01, September 2026).
**No breaking API changes** — code consuming 1.2 compiles and runs unchanged. Files written by 1.2
and earlier remain readable forever. What changed is the bytes the writer emits and one runtime
tightening, listed first because it is the only change that can turn a working call into an error:

### Behavior changes that can surface at runtime

- Unit validation now validates images. Two new errors: duplicate `Image` `id` attributes within a
  unit (a Revision 1 requirement), and a byte-shuffling (`+sh`) codec selected without an item size.
  A unit that previously wrote without complaint can now fail validation for these two reasons only.
- The property identifier rule was corrected per the spec: namespaced identifiers with single colons
  (`Observation:Time:Start`, `AstrometricSolution:Version`) now validate. This is a loosening.

### Changed (emitted file format — all spec-conformant, readable by 1.2)

- Headers are XML-namespaced: the root and all core elements are emitted in
  `http://www.pixinsight.com/xisf`, so output now validates against the official XSD.
- String properties (metadata included) serialize as element character data, never a `value`
  attribute, and embedded whitespace/newlines round-trip exactly.
- The `compression` attribute carries an item-size component only for `+sh` codecs.
- Data block checksums are emitted when `XisfWriterOptions.CalculateChecksums` is set (previously the
  option did nothing). The default is unchanged: no checksums unless opted in.

### Added

- All 8/16/32/64-bit integer scalar property types read and write (previously Int32/UInt32 only).
- Vector and matrix property types (all integer widths, Float32/Float64), including block-backed
  values (`location="attachment:..."` etc.), empty vectors/matrices, and both primary and alternate
  spec type names on read.
- Big-endian data blocks: the `byteOrder` attribute is honored on read (output remains
  little-endian).
- Inline and embedded data block content is read (previously silently empty).
- Compression subblocks (`subblocks` attribute) are decoded.
- Boolean properties accept `1`/`0` per Revision 1; non-finite floating point values accept every
  spec form (`NaN`, `+Inf`, `-Inf`, `nan`, `inf`, ...) and emit the canonical forms.
- Unknown-object preservation: unrecognized root/image child elements and unrecognized property
  types survive read → write verbatim (`XisfHeader.UnknownElements`, `XisfImage.UnknownElements`,
  `XisfRawProperty`) instead of being dropped. This makes round-tripping PixInsight 1.9.5+
  `AstrometricSolution:*` property sets lossless — the library carries solutions; it does not
  evaluate them.
- Partial-failure policy: a block using an unsupported compression codec or checksum algorithm no
  longer fails the whole unit — the header parses, sibling blocks read, and materializing the
  affected block throws `XisfUnsupportedObjectException`.
- The Revision 1 metadata properties `XISF:BlockAlignmentSize`, `XISF:ChecksumAlgorithms`,
  `XISF:MaxInlineBlockSize`, `XISF:OutputHints`, and serialization of all previously-modeled
  metadata fields (title, authors, copyright, ...).

### Interoperability notes

- Zstandard (`zstd`, `zstd+sh`) is fully supported for read and write but is never chosen by
  default: decoders predating Revision 1 (including PixInsight before 1.9.5) cannot read
  zstd-compressed blocks. Opt in per write.
- Property format specifiers remain carried verbatim, not applied. Distributed (`.xish`/`.xisb`)
  writing remains header-only.

### Fixed

- Regression in 1.3: `ReadMetadataFromStream`/`ReadMetadataFromStreamAsync` threw
  `EndOfStreamException` when given a stream holding only the file header (16 bytes + XML header)
  and a property stored its value in an attached block past that point. WBPP 3.1 / PixInsight 1.9.5
  masters store `PixInsight:ProcessingHistory` this way. Metadata reads now resolve an attached
  property block only when it lies within the stream (seekable, and `position + size <= Length`).
  Otherwise the property keeps its Id and comes back as a placeholder (an empty
  `XisfStringProperty` value, or an empty vector/matrix) whose new `XisfProperty.UnresolvedBlock`
  (init-only, `null` when resolved) holds the declared `AttachedDataBlock`: position, size,
  compression, checksum and byte order. Unresolved blocks are never read, so their checksums are
  not validated. Blocks inside the stream resolve as before, and `ReadAsync` is unchanged.
- Writing a unit that contains an unresolved property throws `InvalidOperationException` naming
  the property Id, so a metadata-only read can never be written back with its values blanked. A
  `with` expression carries the mark; set `UnresolvedBlock = null` when supplying the value.

## 1.2

Switched to automated CI versioning and publishing: every push to main publishes
`1.2.<run>` to nuget.org and tags it. No library behavior changes.

## 1.1

Last manually-versioned line (final release 1.1.3). 1.1.3 fixed checksum attribute parsing to accept
the spec's hyphenated algorithm names (`sha-1:...`), as written by N.I.N.A. and PixInsight.
