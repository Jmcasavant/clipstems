# Releasing Clipstems

Releases are built by GitHub Actions from a version tag. Installed copies
find a new release within about six hours and install it the next time
Clipstems exits.

## Cut a release

1. Make sure `main` is green.
2. Tag the commit with the version (SemVer) and push the tag:

   ```bash
   git tag v0.3.0
   ```

   ```bash
   git push origin v0.3.0
   ```

3. The `release` workflow builds, tests, and packages the release, then
   publishes it on GitHub with `ClipstemsApp-win-Setup.exe`, a portable zip,
   the update packages, and the update feed (`releases.win.json`).

A tag with a pre-release suffix (`v0.3.0-beta.1`) publishes a GitHub
pre-release. Installed copies don't update to pre-releases.

Never delete a published release's files. Installed copies build delta
updates from the previous release.

## Try a build before releasing

Every push and pull request runs the `package` job, which uploads
`Clipstems-Setup-ci.<run>` as a workflow artifact for a week. That build is
versioned `0.0.1-ci.<run>` (Velopack needs at least 0.0.1), so the first real release replaces it through a
normal update.

To build locally on Windows (PowerShell 7 and the .NET 10 SDK):

```bash
pwsh ./build/package.ps1 -Version 0.3.0-local.1
```

The output is in `artifacts/Releases`.

## Update the bundled FFmpeg

Edit `build/ffmpeg.json`; its `$comment` explains how. Pin a month-end
`autobuild-*` release from BtbN/FFmpeg-Builds, because daily builds are
deleted after a few weeks. The packaging script refuses a download whose
SHA-256 doesn't match.

## Bump Velopack

Change the `Velopack` package version in `src/Clipstems.App/Clipstems.App.csproj`.
`build/package.ps1` installs the `vpk` tool at the same version.

## Code signing

The installer is currently unsigned; see "Installer and updates" in
[APP.md](APP.md). To sign, make one of these available to the packaging step:
`VPK_SIGN_PARAMS` (arguments for `signtool.exe sign`, with the certificate on
the runner) or `VPK_AZURE_TRUSTED_SIGN_FILE` (Azure Trusted Signing metadata,
after `azure/login`).
