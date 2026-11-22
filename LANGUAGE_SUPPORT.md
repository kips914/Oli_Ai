# OLI language support

OLI v1.3.4 supports Russian and English UI languages.

## Application

Open **Settings → Language** and select:

- Automatic (system)
- Русский
- English

The selected value is saved in the normal OLI settings file. `Auto` resolves from the Windows UI culture.

## Installer

`Install-OLI.ps1` accepts:

```text
-Language auto
-Language ru
-Language en
```

The default is `auto`.
