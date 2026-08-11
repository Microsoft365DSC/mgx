# Changelog

## 1.0.3

- Fixed `Remove-Module Mgx` failing and leaving the module loaded. Static-state cleanup moved into `AlcInitializer.OnRemove`, ahead of the ALC resolver detaching; it previously ran from the module `OnRemove` scriptblock, by which point `Polly.Core` was unresolvable. Only triggered when no Graph request had run in the session
- Fixed the `SdkVersion` request header reporting `mgx/0.3.0` regardless of the installed version. It now matches the module version, and `build.ps1` fails the build if the constant and the manifest ever disagree
- Extracted cmdlet lifecycle and JSON conversion to `MgxCmdletCore`. Internal refactor; no change to the cmdlet surface
- Treat `CA1416` as an error in both projects, so a Windows-only API reaching the cross-platform code paths fails the build instead of throwing `PlatformNotSupportedException` on Linux or macOS

## 1.0.2

- Fixed Linux install: renamed `Mgx.psd1`, `Mgx.psm1`, and `Mgx.Format.ps1xml` to lowercase so `Install-Module Mgx` works on case-sensitive filesystems (PSGallery lowercases the module folder name)
- Updated `about_Mgx_Tuning` version reference to v1.0.1

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`