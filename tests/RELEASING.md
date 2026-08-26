# Releasing

`main` carries one commit per release. There are no merge commits: a release is `dev` squashed
onto `main`, tagged, then published.

Every command below runs from the repository root, not from this directory.

## Before

1. **Both suites, both platforms.**

   ```powershell
   Import-Module ./tests/TestHarness.psm1 -Force
   Invoke-TestHarness
   ```

   One call runs the xUnit and Pester suites and prints the same C# and PowerShell sections CI
   writes to the run summary. The E2E suite is not included, since it needs a Linux container
   host.

   Run them on Windows too. A green run on one OS says nothing about file locking, path
   comparison or the exceptions Windows raises where Unix raises different ones - each of those
   has shipped a defect here.

2. **A real tenant, by hand.**
   This fork does not ship a live Pester suite. The mocked suites cannot see a request Graph
   rejects: `Enable-MgxResilience` once shipped a client with no `BaseAddress`, breaking every
   relative-URI call, with every test green. Before a release, connect to a tenant and run each
   cmdlet once, including a paged read, a batch write and a delta sync.

3. **Install it the way a user does.** Build Release, then in a *fresh* shell with only the
   Gallery dependencies present, import the staged `Modules/M365DSC.mgx/`, run `Test-ModuleManifest`, and
   check `Get-Help` for every exported cmdlet. Stale generated help drops parameters silently.

4. **Read the published numbers.** Every figure in `README.md` must be reproducible from
   `tests/benchmarks` as it stands. Re-measure rather than reuse: the tenant's throttling
   ceiling has moved by 40% between consecutive days.

5. **Version and notes.** `ModuleVersion` in `Modules/M365DSC.mgx/M365DSC.mgx.psd1`, the manifest's `ReleaseNotes`,
   and the `CHANGELOG.md` section all agree.

## Squashing

```bash
git checkout main && git pull origin main
git merge --squash dev
git commit -F <release message>

git diff --stat dev        # MUST be empty - if it prints anything, the squash lost something

git tag -a vX.Y.Z -m "mgx X.Y.Z"
git push origin main && git push origin vX.Y.Z
```

The squash message is the public record of the release; the CHANGELOG carries the per-defect
detail. Describe what the release *is*, not the churn that produced it.

## After

`Publish-Module` from the tagged `main`.

`dev` and `main` share no history once squashed, so `dev` reads as permanently ahead. Retire it
and branch a fresh one from `main` for the next version rather than carrying the divergence.
