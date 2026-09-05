# OLI Mobile 1.6.1 — Codemagic

1. Put this repository in GitHub/GitLab/Bitbucket (or another Git repository supported by Codemagic).
2. Keep `codemagic.yaml` in the repository root.
3. In Codemagic, add the repository as an application.
4. Choose Android/native project and let Codemagic scan the repository.
5. Select the workflow `oli-mobile-android`.
6. Start a build.
7. After a successful build, download the APK from the Artifacts section.

The project currently builds an unsigned release APK and a debug APK. For installing on a phone for testing, use the debug APK first. For a distributable release, add a private Android keystore in Codemagic and configure release signing; never commit the keystore to the repository.
