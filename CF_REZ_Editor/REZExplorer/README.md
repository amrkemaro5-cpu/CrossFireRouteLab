# CF REZ Editor — merged development direction

The CF REZ Editor keeps the **original English CF REZ Editor UI as the baseline**. The REZ Explorer work is now an internal capability layer rather than a replacement interface.

## Merge rule

- Preserve the original CF REZ Editor layout, colors, controls and CFT/CSV editor.
- Add new readers and analysis behind the existing UI.
- Do not replace the main application with the experimental REZ Explorer prototype.
- Do not invent DAT/UTC field meanings; show structural evidence until the schema is proven.

## Integrated capabilities

- Tolerant REZ reading: invalid resource ranges remain visible instead of aborting the entire archive.
- CrossFire REZ directory crypto/reader support.
- Resource Information with MD5/SHA256, entropy, signatures, strings and codec evidence.
- Resource Studio: Info, Hex, Strings, Reader, Editor and exact-size REZ replacement.
- LZMA-Alone detection/decode-to-copy.
- CFT table editor remains the existing editor and supports CSV/XLSX workflows.
- FNT structural reader.
- LTA/BF005 experimental fixed-record reader (68-byte records / 17 DWORD fields).
- LTO/LithTech Object PE reader.
- DAT/UTC evidence reader without guessed schemas.
- File comparison and first-difference reporting.
- Conservative repacking: the original repacker is only used when the archive passes the strict structural checks; damaged/ambiguous archives remain inspectable and support same-size resource replacement.

## Supplied-file evidence

- `RB001.REZ`: 58,758,998 bytes; 1,105 readable entries and 2 invalid-range diagnostics in the tolerant reader.
- `Object.lto`: PE/LithTech object image with 5 sections.
- `bf000.lta`: 34,002 bytes; 500 fixed 68-byte records plus a 2-byte remainder.
- `GOOLIM12.fnt`: 567,105-byte binary font resource; structural/raw reader only until its glyph schema is proven.

The experimental REZ Explorer UI remains useful as a design/reference prototype, but it is **not** the replacement for the original CF REZ Editor UI.
