# Changelog

## 2.0.3

- Removed manifest dependency on `Microsoft.Graph.Authentication` to prevent updating it to a newer version than what is already installed.

## 2.0.2

- Fixed an issue where a JSON string passed to `-Body` was silently dropped. PowerShell does not unwrap `PSObject` when binding to an `object`-typed parameter, so `-Body (@{...} | ConvertTo-Json)` arrived wrapped, missed the string branch of the serializer, and was serialized as `{}`. The request went out with an empty body and Graph answered with an empty result, without an error anywhere.
- Write responses that carry a collection envelope (`{"value":[...]}`), as returned by action endpoints such as `/directoryObjects/getByIds`, `/getMemberGroups` and `/checkMemberGroups`, are now unwrapped into one object per element, matching what GET already did.
- Added `-Debug` request/response tracing to every cmdlet, covering single requests, pagination, fan-out and `$batch`.
- Fixed the `SdkVersion` header, which still announced `mgx/0.3.0` on every Graph request. It is now derived from the assembly version, set once in `Directory.Build.props`, and a test fails the build if it ever drifts from `ModuleVersion` in the module manifest.
- `-Body` on a GET request now warns instead of being silently ignored, an empty or whitespace body falls back to `{}` instead of sending a zero-length JSON request, and a batch item whose body is not valid JSON now fails on its own instead of aborting the whole batch.

## 2.0.1

- Fixed an issue where Mgx cmdlets kept using the credentials of the first `Connect-MgGraph` call in a session. The cached HTTP client was keyed on tenant id alone, so reconnecting to the same tenant with a different application, certificate, account, or scope set silently reused the previous identity and its permissions.
- Fixed an issue when `Enable-MgxResilience` was active. Its wrapper around the Microsoft.Graph SDK client stayed bound to the pre-reconnect client. Resilience is now re-injected automatically when the identity changes. If that is not possible, a warning names `Enable-MgxResilience` as the fix.
- The current auth context is now read from `GraphSession` directly instead of invoking `Get-MgContext` on every request, removing a PowerShell runspace round-trip from the hot path. `Get-MgContext` remains the fallback.
- Fixed an issue where a single throttling episode slowed `Invoke-MgxBatchRequest` for the rest of the session. A 429 halves the write pacing rate and the reduced rate is persisted across calls, but nothing ever raised it again. The rate now recovers after two consecutive chunks without a 429, it adds back a tenth of the configured rate, and five minutes without any throttling restores the configured rate outright. Both adjustments are reported under `-Verbose`.
- Fixed an issue where `Set-MgxOption -TotalTimeoutSeconds` did not reach the HTTP client. `HttpClient.Timeout` is fixed once the first request has been sent, so the cached client kept the timeout it was built with. The client is now rebuilt when the value changes.
- Fixed an issue where the internal type cache was never invalidated. Re-importing `Microsoft.Graph.Authentication` (a different version, or into a fresh load context) left Mgx resolving `GraphSession` to the previous assembly's type, and therefore to a different singleton than the one the SDK was using. The cache is now dropped whenever an assembly loads.

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
