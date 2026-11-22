# OLI 1.3.2 icon integration

## Main application
- Source: `OLI.Client/Assets/OLI.ico`
- Embedded by `OLI.Client.csproj` using `ApplicationIcon`.
- The published `dist/OLI.exe` will use the OLI main icon.

## Installer
- Source: `installer/OLI_Setup.ico`
- Intended for the compiled `OLI_Setup.exe` resource.
- `Install-OLI.ps1` reports the icon path for packaging.

The icons are multi-resolution ICO files (16..256 px).
