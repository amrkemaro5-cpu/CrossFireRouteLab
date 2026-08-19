# REZ Explorer direction

The CF REZ Editor is being moved toward the supplied REZ Explorer reference layout while keeping the interface English.

Verified supplied resources:

- `RB001.REZ`: 58,758,998 bytes; CrossFire directory crypto corrected; 1,105 readable entries and 2 invalid-range diagnostics.
- `Object.lto`: PE/LithTech object image with 5 sections.
- `bf000.lta`: 34,002 bytes; 500 fixed 68-byte records plus 2-byte remainder; exposed as 17 little-endian DWORD fields per record for experimental editing/research.
- `GOOLIM12.fnt`: 567,105-byte binary font resource; currently exposed through structural/raw analysis without asserting an unverified glyph schema.

The UI prototype has a merged directory tree, current directory, search, file list, Info/Reader/Editor/Hex/Analysis views, visible status, loose-resource opening, REZ extraction/inspection, and conservative same-size replacement.

The generated reference screenshot is English and uses the CrossFire logo/REZ Explorer visual direction.
