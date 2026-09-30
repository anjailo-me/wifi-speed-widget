# Packaging for the Microsoft Store

The Store signs the package, so Smart App Control and SmartScreen let it run. This page covers building the package.

## Identity values

From the product's Product identity page in Partner Center:

| Partner Center | Build script option |
| --- | --- |
| Package/Identity/Name | `-IdentityName` |
| Package/Identity/Publisher (looks like `CN=xxxxxxxx-xxxx-...`) | `-Publisher` |
| Package/Properties/PublisherDisplayName | `-PublisherDisplayName` |

## Build

Requires the .NET 10 SDK and Microsoft Edge (used once to draw the icons). From the repository root:

```
powershell -ExecutionPolicy Bypass -File .\Packaging\make-assets.ps1
powershell -ExecutionPolicy Bypass -File .\Packaging\build-msix.ps1 -IdentityName "<name>" -Publisher "<CN=...>" -PublisherDisplayName "<display name>" -DisplayName "Speedline" -PrivacyUrl "<public address of the privacy policy>" -Version "1.2.0.0"
```

`-PrivacyUrl` adds a "Privacy policy" item to the widget's right-click menu. The package is written to `dist\`. Raise `-Version` for every update; the last number must stay 0. The icons only need to be redrawn if `assets\logo.svg` changes.

The first build downloads Microsoft's `makeappx` tools from NuGet (about 60 MB).

## Submit

Upload the `.msix` under Packages in a new submission. Partner Center may warn that the package is unsigned; that is normal.

The package uses the `runFullTrust` capability (a desktop app with a borderless, always-on-top widget that reads Wi-Fi details) and the `location` capability (Windows only shares the Wi-Fi network name with apps that have location permission; the app does not read or store the device's location).

Screenshots and the app icon for the listing are in `Packaging\store`.
