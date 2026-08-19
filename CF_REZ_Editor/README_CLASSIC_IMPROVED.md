# CF REZ Editor — Classic Design / Format Engine Expansion

This development line keeps the original English CF REZ Editor identity and adds reader/analysis functionality instead of redesigning the UI.

## Current fixes
- Tolerant REZ resource-range handling: one bad resource no longer prevents the archive from opening.
- Invalid-range resources remain visible and are explicitly marked; they are not extracted or modified automatically.
- Visible workspace status plus archive status information.
- LZMA-Alone detection/decode.
- CFT external XOR(0x10) decode/encode.
- CFT schema parsing and writing.
- CFT <-> CSV and CFT <-> XLSX conversion.
- ASCII and UTF-16 string extraction.
- entropy, magic/signature and hash analysis.
- DAT/UTC evidence-only analysis until their binary schemas are proven.
- file comparison and resource analysis reports.
- conversion/batch-tools hub.

## Design rule
Do not replace the classic interface with a new theme. Add knowledge and functionality to the existing editor.

## Safety rule
Unknown formats are not presented as decoded just because a heuristic looks plausible. Evidence is shown until a format is verified.

## Test status
The local source package has passed:
- Python compilation
- tolerant REZ parser test
- invalid-range retention test
- CFT parse/encode test
- CFT CSV round-trip test
- CFT XLSX round-trip test
- resource-analysis test
- GUI startup/shutdown test under Xvfb

A native Windows EXE is not claimed from the Linux build environment; Windows runtime testing must be performed on Windows.
