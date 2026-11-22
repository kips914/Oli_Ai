# OLI 1.3.4 — GitHub Releases setup

The application checks the latest **non-prerelease** GitHub Release through the GitHub Releases API.

## 1. Set the repository URL

Open OLI → Settings → Updates and enter:

`https://github.com/YOUR_USERNAME/YOUR_REPOSITORY`

The value is saved to the normal OLI settings file under `%LOCALAPPDATA%\OLI\settings.json`.

## 2. Publish releases

Create GitHub Releases with semantic version tags such as:

- `v1.3.4`
- `v1.3.4`
- `v1.4.0`

The checker ignores prereleases and compares the numeric version against the application version.

## 3. Updating files

The checker intentionally does **not** replace OLI.exe by itself. When an update is available it opens the GitHub release page. The existing OLI GUI installer/updater remains responsible for replacing application files while preserving user data and models.

## 4. Recommended release asset

For each public release, upload the built release ZIP containing the GUI installer/update package. The update checker does not depend on a fixed asset filename, so the release can also contain documentation or checksums.
