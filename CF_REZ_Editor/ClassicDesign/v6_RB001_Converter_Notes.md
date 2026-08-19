# CF REZ Editor v6 notes

The supplied English CF REZ Editor layout remains the UI baseline. This iteration adds reader/converter capability behind that UI rather than replacing the design.

## RB001 fallback

The normal LithTech/CrossFire directory parser is attempted first. If the protected/variant directory cannot be decoded, the editor can fall back to the CrossFire native packer (`cfrez.exe`, `cfrezformat.dll`, or `pack_cf_*.dll`) and display the extracted resources in the existing REZ table. Native-fallback archives are read/extract only until a matching native writer is integrated.

The public `00x16/cfrez.exe` source documents the `xv` extraction command and notes that `cfrezformat.dll` corresponds to the game's `pack_cf_{00}.dll`.

## Converter additions

- MSZ -> TXT string extraction
- SCV -> CSV evidence export
- LTC -> LTA using the CrossFire 16-byte XOR layer and LithTech LTC bitstream decoder
- LTA -> CSV fixed-record export
- PNG -> DTX common LT2-style uncompressed template
- DTX -> PNG
- DTX -> DDS

MSZ/SCV remain evidence-first where client variants are not proven. DTX and LTC have concrete decoding paths based on the open-source CFRezManager implementations.

## Test status

- Python compilation: PASS
- GUI startup/shutdown under Xvfb: PASS
- RB001 native fallback adapter with mocked cfrez.exe: PASS
- LTC decode test: PASS
- PNG -> DTX -> PNG/DDS tests: PASS
- MSZ/SCV/LTA reader tests: PASS
