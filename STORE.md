# Publishing Speedline to the Microsoft Store

The Store signs the package for you, so Smart App Control and SmartScreen let it run for everyone. This page walks through the submission and has the text to paste into each box.

## Before you start

- A Microsoft account. Register as an individual developer at https://partner.microsoft.com/dashboard. You verify your identity with an ID. Check the sign-up page for any registration fee.
- If you will charge for the app: set up your tax profile and payout details under Account settings in Partner Center, and check that payouts are supported in the Philippines.
- A public web address for the privacy policy. [PRIVACY.md](PRIVACY.md) is written for this. Add your contact email to it first. GitHub Pages works if the repository is public. If you want to keep the repository private, put the text on any public page instead, such as a Notion page shared to the web.

## 1. Reserve the name

In Partner Center choose Apps and games, then New product, then MSIX or PWA app. Reserve the name. "Speedline" is a working name and may already be taken. Partner Center tells you straight away. Some other ideas: Speedline Widget, Linkpulse, Pingdeck. None of these have been checked. Avoid putting "Wi-Fi" in the name, because it is a trademark of the Wi-Fi Alliance.

## 2. Copy the three identity values

Open the reserved product, then Product management, then Product identity. Copy:

| Partner Center | Build script option |
| --- | --- |
| Package/Identity/Name | `-IdentityName` |
| Package/Identity/Publisher (looks like `CN=xxxxxxxx-xxxx-...`) | `-Publisher` |
| Package/Properties/PublisherDisplayName | `-PublisherDisplayName` |

## 3. Build the package

Requires the .NET 10 SDK and Microsoft Edge (used once to draw the icons). Run from the repository root, using your own values:

```
powershell -ExecutionPolicy Bypass -File .\Packaging\make-assets.ps1
powershell -ExecutionPolicy Bypass -File .\Packaging\build-msix.ps1 -IdentityName "<name>" -Publisher "<CN=...>" -PublisherDisplayName "<display name>" -DisplayName "<the reserved name>"
```

The icons only need to be redrawn if you change `assets\logo.svg`; the generated ones are already in the repository. The package is written to `dist\`. For a later update, raise `-Version` (for example `1.1.1.0`). The last number must stay 0.

The first build downloads Microsoft's `makeappx` tools from NuGet (about 60 MB).

## 4. Fill in the submission

**Pricing and availability.** Choose your markets. To sell it, pick a price. For the Philippines the price list may not have exactly 59 pesos; use the closest one. Microsoft keeps a share of each sale. You can offer a free trial.

**Properties.**
- Category: Utilities & tools
- Privacy policy URL: the public address of your privacy policy
- Website and support contact: optional

**Age ratings.** Answer the questionnaire honestly. The app has no violence, chat, purchases or user-generated content, so it should come out as suitable for everyone.

**Packages.** Upload the `.msix` from `dist\`. Partner Center may show a warning that the package is unsigned. That is normal; the Store signs it.

**Store listing (English).**

Short description:

> A small desktop widget that tests your internet speed and watches your connection quality.

Description:

> Speedline puts your connection on your desktop. A small card shows your download and upload speed, ping and jitter, and gives your Wi-Fi a live rating from Excellent to Poor.
>
> - Automatic speed tests every 5, 15, 30 or 60 minutes, or only when you ask
> - Live ping, jitter and packet loss, updated every couple of seconds
> - Wi-Fi network name, band and signal strength
> - A history of your recent tests
> - Pauses automatic tests on metered connections
> - Drag it anywhere, keep it on top, and start it with Windows
> - Follows your Windows light or dark theme and accent color
>
> Speed tests use the open Measurement Lab (M-Lab) network. There is no account, no ads and no tracking.

What's new:

> First release.

Keywords (up to seven): speed test, internet speed, wifi, ping, network, widget, bandwidth

Images:
- App tile icon: `Packaging\store\icon-300x300.png`
- Screenshots (1920 x 1080): `Packaging\store\screenshots\` (three images)

**Notes for certification.**

> No sign-in is needed. Click "Test now" on the widget to run a speed test; it needs an internet connection and uses the open M-Lab measurement servers. The Wi-Fi name shown on the widget comes from Windows and may be blank if Location access for desktop apps is off in Windows Settings.

**Location capability.** The package declares the location capability because Windows only shares the Wi-Fi network name with apps that have location permission. If Partner Center asks why:

> Windows requires location permission for an app to read the name of the Wi-Fi network it is connected to. The app shows that name on its widget. It does not read, use or store the device's physical location.

**Restricted capability (runFullTrust) justification.**

> The app is a desktop (WPF) application. Full trust is needed for a borderless, always-on-top desktop widget and to read the current Wi-Fi details from Windows.

## 5. Submit

Choose Submit to the Store. Review usually takes a few days. You are told by email and in Partner Center if something needs changing.

## After the first release

- For updates, raise the version, rebuild, and start a new submission with the new package.
- The app has been built and its package checked with Microsoft's `makeappx`, but it could not be run on the build PC because Smart App Control blocks unsigned code there. The first real test is the Store version. If you want more assurance first, the Windows App Certification Kit in the Windows SDK can be run against the package.

## Before you charge money

- M-Lab provides the speed tests free of charge as a public service. Read its usage terms at https://www.measurementlab.net/ before selling an app that depends on it, and consider contacting them.
- Keep the source repository private if you sell the app. A public repository with a free build would undercut it.
