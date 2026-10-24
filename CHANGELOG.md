# Changelog

Notable changes per minor version (patch numbers are CI run numbers and carry no meaning).
Format loosely follows [Keep a Changelog](https://keepachangelog.com/).

## 1.4

XISF 1.0 **Revision 2** conformance release (spec document v1.02, October 2026). Revision 2 defines
how incompressible compression subblocks are stored: a subblock whose compressed and uncompressed
lengths are equal holds uncompressed data. **No breaking API changes**: code consuming 1.3 compiles
unchanged (one optional reader option is added). The runtime tightenings come first, because they
are the only changes that can turn a working call into an error:

### Behavior changes that can surface at runtime

- Every compressed subblock, and a compressed block without a `subblocks` attribute, must
  decompress to exactly its declared length. A zlib stream that produced more data than declared
  was silently truncated before; it now fails with `InvalidDataException`.
- The `subblocks` attribute must account for the block exactly: its compressed lengths must sum to
  the stored block size and its uncompressed lengths to the `compression` attribute's size. A
  shortfall was silently zero-padded before; it now fails with `InvalidDataException`.
- Every malformed compressed block fails with `InvalidDataException`. Corrupt Zstandard data no
  longer leaks `ZstdException`, corrupt zlib data whose header requests a preset dictionary no
  longer leaks the runtime's zlib `IOException`, and subblock lengths that overrun the block no
  longer surface as `ArgumentException` or `ArgumentOutOfRangeException`.
- A malformed `subblocks` attribute is no longer ignored. As with a malformed `compression` or
  `checksum` attribute, the header still parses and materializing that block throws
  `XisfUnsupportedObjectException`. A `subblocks` attribute without `compression` stays ignored.
- An unparsable item-size component on a byte-shuffling (`+sh`) codec is no longer dropped, which
  read the block without unshuffling and returned wrong pixels. It is now handled like a malformed
  `compression` attribute: the header parses and materializing the block throws
  `XisfUnsupportedObjectException`. A third component on a non-shuffling codec stays ignored.
- A block too large for one managed array (about 2 GiB) throws `NotSupportedException` before any
  of it is read, instead of failing on allocation with `OutOfMemoryException`. This covers a
  compressed block's declared uncompressed size and an attached block's stored size. Such blocks
  were never readable; header and metadata reads are unaffected.

### Changed (emitted file format, readable by 1.3 and every earlier decoder)

- The writer never stores data that compression made larger. A block that does not shrink under
  the requested codec is written uncompressed, without a `compression` attribute, holding the
  original (unshuffled) bytes. Under 1.3 such blocks carried slightly expanded compressed data.

### Added

- Uncompressed subblocks are read: a subblock with equal compressed and uncompressed lengths is
  copied unmodified, including the implicit single subblock of a block without a `subblocks`
  attribute. This is how PixInsight stores incompressible pixel data, which 1.3 could not read.
  Under byte-shuffling codecs the copied bytes are part of the shuffled stream, unshuffled once
  after all subblocks are concatenated. Subblocks longer than their uncompressed data are still
  decompressed.
- Legacy tolerance: an equal-length subblock is first decompressed, and the result used when it
  yields exactly the declared length; otherwise it is copied raw. Encoders predating Revision 2
  (XisfLib.Core 1.3 and earlier among them) could write genuinely compressed equal-length
  subblocks. `XisfReaderOptions.TryDecompressEqualLengthSubblocks` (default `true`) turns the
  probe off for strict Revision 2 reading.
- Explicit subblocks (a caller-supplied `XisfCompression.Subblocks` list) store each subblock that
  does not shrink as an uncompressed subblock. When none shrinks, the block is written without
  compression.

### Interoperability notes

- Files written with explicit subblocks may contain uncompressed subblocks, which decoders
  predating Revision 2 other than PixInsight cannot read (for example libXISF/Siril, N.I.N.A. and
  the Python `xisf` package decompress every subblock). The default writer never splits a block,
  so it never emits them.
- Residual risk of the legacy probe: a raw LZ4 subblock that happens to be a valid LZ4 stream of
  exactly its own length would be read as compressed data. This is practically unreachable for
  image data; set `TryDecompressEqualLengthSubblocks` to `false` to rule it out.

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
