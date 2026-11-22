# OLI v1.3.4_released

## English language support

- Added `OliLanguage`: Auto, Russian, English.
- Added a language selector to OLI Settings.
- The main window, web-search panel, console controls, settings window, update dialogs and maintenance dialogs are localized.
- Auto mode follows the Windows UI culture and uses English for `en-*`, Russian otherwise.
- Language is saved in `settings.json` and survives updates.
- Installer scripts now support `-Language auto|ru|en`.
- The installer automatically detects the Windows UI language when `auto` is selected.
- `Install-OLI.ps1` passes the selected language to the CUDA/llama.cpp installer.
- Updated application version to `1.3.4_released`.

## Usage

```powershell
# Automatic language detection
powershell -ExecutionPolicy Bypass -File .\Install-OLI.ps1 -Language auto

# Force English
powershell -ExecutionPolicy Bypass -File .\Install-OLI.ps1 -Language en

# Force Russian
powershell -ExecutionPolicy Bypass -File .\Install-OLI.ps1 -Language ru
```
