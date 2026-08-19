# CF REZ Editor development overlay

This branch contains the development overlay for the user's existing **CF REZ Editor**.

## Important design rule

The existing CF REZ Editor UI remains the source of truth. This project does **not** replace its English layout, colors, toolbar, CFT editor, REZ explorer, or existing commands.

The overlay adds format-analysis capabilities while preserving the existing REZ/CFT engine.

## Current fixes

- Fixed the packaged Tkinter startup error caused by calling `tkinter.mainloop()` without a default root.
- The launcher now explicitly creates `App()` and calls `App().mainloop()`.
- Resource Information reads resource bytes from the archive path/entry offset instead of assuming an unavailable `RezArchive.raw` attribute.
- Added conservative resource analysis for signatures, LZMA-Alone, CFT validation, ASCII/UTF-16 strings, entropy, and 32-bit integer profiles.
- DAT/UTC are reported as structural candidates only; no unverified schema is claimed.

## Testing

The development build is checked with:

- PyInstaller CArchive/package geometry validation
- preservation of the original embedded UI/core modules
- Tkinter GUI smoke test under Xvfb
- explicit `__main__` startup test
- Resource Information path test
- analyzer unit tests
- PE format validation

The final Windows executable must still be run on Windows for a real end-user smoke test.
