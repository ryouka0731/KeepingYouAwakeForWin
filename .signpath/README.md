# Code signing setup (SignPath Foundation)

Releases are signed by [SignPath Foundation](https://signpath.org) through the `Sign with SignPath` step in [`release.yml`](../.github/workflows/release.yml). Until the `SIGNPATH_API_TOKEN` secret exists, that step is skipped and releases stay unsigned.

## One-time setup

1. **Prerequisites** — a published release (the form that will be signed), the *Code signing policy* section in the README, and multi-factor authentication on the GitHub account (SignPath requires MFA for everyone with access).
2. **Apply** at <https://signpath.org/apply.html> with the repository URL. Wait for approval; SignPath Foundation creates an organization and a project for you.
3. **In SignPath**:
   - Project slug: `KeepingYouAwakeForWin` (or set the `SIGNPATH_PROJECT_SLUG` variable below).
   - Add the trusted build system *GitHub.com* to the project (origin verification).
   - Artifact configuration: paste [`artifact-configuration.xml`](artifact-configuration.xml). Its slug defaults to `initial`.
   - Signing policy `release-signing` with yourself as approver.
   - Create a CI user (or API token) with *submitter* permission on that policy.
4. **In GitHub** (Settings → Secrets and variables → Actions):
   - Secret `SIGNPATH_API_TOKEN` — the CI user's API token.
   - Variable `SIGNPATH_ORGANIZATION_ID` — from SignPath's organization settings.
   - Optional variables if your slugs differ: `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG`, `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG`.

## Releasing

1. Push a tag `vX.Y.Z` (the version must match the build: the artifact configuration restricts `product-version` to it, and the release build sets the executables' ProductVersion to exactly `X.Y.Z`, or `X.Y.Z-label` for pre-release tags). If SignPath rejects requests over the version, check how it reads ProductVersion and adjust the `product-version` attribute.
2. The release workflow builds both executables and submits them to SignPath.
3. Approve the signing request in SignPath within two hours (the workflow waits that long).
4. The workflow checks both signatures, zips the signed executables and uploads them to the GitHub Release.

If the request timed out or was rejected, use *Re-run failed jobs*, or *Actions → Release → Run workflow* with the **tag selected under "Use workflow from"**. Signing never runs from a branch: the workflow fails if it isn't running on the tag ref.
