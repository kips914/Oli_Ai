# OLI Mobile

The phone client now lives in [`Android/OLI-Mobile`](../Android/OLI-Mobile) (Kotlin + Jetpack Compose).
It talks to **OLI.Server** running on the user's PC — see the main [README](../README.md) and
[ARCHITECTURE.md](../ARCHITECTURE.md).

* APK: `OLI-Mobile-<version>-release.apk` in the GitHub release (built by
  `.github/workflows/build-apk.yml`).
* Sign in / register with a personal account; the server stores only a PBKDF2 hash.
* Each account gets its own workspace with a 1 GB quota, browsable from the app.

`OLI.MobileGateway` (an earlier prototype bound to `127.0.0.1`) is superseded by
`OLI.Server` and is kept only for reference.
