# Changelog

## 2.0.0

**Breaking changes.**

- **BREAKING:** The module is now published as `M365DSC.mgx` instead of `mgx`. Install with `Install-Module M365DSC.mgx` and import with `Import-Module M365DSC.mgx`. The manifest, root module, and format file were renamed to match. Cmdlet names are unchanged (`Invoke-MgxRequest`, `Get-MgxTelemetry`, ...), so only the install/import lines in existing scripts need updating.
- **BREAKING:** `Invoke-MgxRequest`, `Invoke-MgxBatchRequest`, `Expand-MgxRelation`, and `Sync-MgxDelta` now emit case-insensitive `Hashtable`s instead of PSObjects, so results work directly with `-is [hashtable]`, `.ContainsKey()`, and splatting. Two consequences:
  - Graph property order is no longer preserved (a `Hashtable` is unordered).
  - The `Mgx.User`, `Mgx.Group`, `Mgx.Application`, `Mgx.ServicePrincipal`, `Mgx.DirectoryRole`, and `Mgx.BatchResult` table views were removed. PowerShell always renders an `IDictionary` with the built-in Name/Value view, so a custom view can never be selected. Use `Format-Table` or `Select-Object` explicitly to pick columns.
- **BREAKING:** The `@odata.type` property is now returned verbatim instead of being renamed to `ODataType`, matching the Graph API response. Code reading `.ODataType` must read `.'@odata.type'` instead. This also fixes round-tripping: a read result piped into `-Body` on PATCH/POST no longer sends a bogus `ODataType` field.
- `Expand-MgxRelation -InputObject` and the `Invoke-MgxRequest` fan-out pipeline parameter now accept hashtables, `PSCustomObject`s, and (for fan-out) bare ID strings. Previously a piped hashtable bound silently as its type name, producing a corrupt URL.
- `Invoke-MgxBatchRequest` accepts hashtables with `Url`/`Method`/`Body` keys, so its own output can be piped back into it.

## 1.0.2

- Fixed Linux install: renamed `Mgx.psd1`, `Mgx.psm1`, and `Mgx.Format.ps1xml` to lowercase so `Install-Module Mgx` works on case-sensitive filesystems (PSGallery lowercases the module folder name)
- Updated `about_Mgx_Tuning` version reference to v1.0.1

## 1.0.1

- Added tab completion for the Uri parameter on all cmdlets that accept Graph API paths (Invoke-MgxRequest, Invoke-MgxBatchRequest, Export-MgxCollection, Expand-MgxRelation, Sync-MgxDelta)
- Extracted `CircuitBreakerMessage` protected property on `MgxCmdletBase` to eliminate repeated inline circuit breaker message strings across six cmdlet files
- Removed redundant XML doc comments on self-documenting members in `MgxCmdletBase` and `ResilientGraphClient`
