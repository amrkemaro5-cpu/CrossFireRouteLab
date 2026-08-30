StageBug Original Runtime Helper

This helper is an analysis/testing companion for the original StageBug runtime.
It does not replace StageBug.exe and does not modify the StageBug.exe file on disk.

Place the helper beside the original StageBug.exe when testing in your own Windows environment.

The helper is guarded by the recovered StageBug runtime signature and is intended to fail closed if the loaded image does not match the analyzed build.

The repository also contains the reconstruction project separately; this folder is the original-runtime analysis helper.