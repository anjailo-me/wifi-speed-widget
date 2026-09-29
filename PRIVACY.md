# Speedline privacy policy

Last updated: 29 September 2026

Speedline is a small Windows widget that tests your internet speed and shows the quality of your connection. This page explains what it does with your information. The short version: Speedline has no accounts, no ads and no analytics, and it does not send anything to the developer.

## What Speedline reads on your PC

- **Wi-Fi details.** It asks Windows for the name of the network you are connected to, its signal strength and its band, so it can show them on the widget. This stays on your PC. Windows requires an app to have location permission before it will share the Wi-Fi network name, so Speedline asks for it for that reason only. It does not read, use or store your physical location.
- **Network activity.** It reads how much data your PC is sending and receiving, to show the live figures. This stays on your PC.
- **Connection type.** It checks whether Windows marks your connection as metered, so it can pause automatic tests. This stays on your PC.

## What Speedline sends over the internet

- **Speed tests.** To measure your speed, Speedline connects to the Measurement Lab (M-Lab) network at `locate.measurementlab.net` and to a nearby M-Lab test server. M-Lab sees your IP address and the test results. M-Lab publishes the results of the tests it runs, including the IP address used, as open data for internet research. Read M-Lab's own policy at https://www.measurementlab.net/privacy/.
- **Connection quality.** Every couple of seconds, Speedline sends a small ping (an ICMP echo request) to `1.1.1.1` (Cloudflare) and, if that fails, `8.8.8.8` (Google). Those operators can see your IP address. Speedline sends nothing else to them.

Speedline does not send the developer any information, does not use analytics or crash-reporting services, and does not show ads.

## What Speedline stores on your PC

Speedline keeps a small settings file in its own app data folder: the position of the widget, your preferences, your last results and a history of your recent tests (time, ping, download and upload speed). It also keeps an error log with technical details of failed tests. None of this contains your Wi-Fi name. Uninstalling Speedline removes this data.

## Children

Speedline is not directed at children and does not knowingly collect information from anyone.

## Changes

If this policy changes, the new version will be published at the same address with a new date.

## Contact

[Add your contact email address here before publishing this page.]
