# ShowLef

A free download manager for audio and video, for Windows, with a Chrome extension.
It downloads with several connections at once, follows HLS/DASH streams, converts audio, and works behind
content filters that inspect HTTPS (it asks Windows which certificate authorities to trust).

Interface languages: Hebrew, French, English.

* Website and installer: https://ext.i-call.me/showlef/
* License: [MIT](LICENSE) — third-party tools: [THIRD_PARTY.md](THIRD_PARTY.md)

## Layout

| Folder | What |
|---|---|
| `app/` | Windows program (C#, .NET Framework 4, compiled with the `csc.exe` that ships with Windows — no packages) |
| `extension/` | Chrome extension (Manifest V3) |
| `tests/` | Test benches (fake HTTPS-inspecting filter, protocol tests) |

## Build

```powershell
.\app\Build-Setup.ps1      # -> dist\ShowLef-Setup.exe (needs 7-Zip for the extension package)
```

No secret is needed to build: the one-time Google sign-in goes through a page of our website (see below), so the program holds no OAuth secret.

## What the program sends over the network

* Downloads: only to the sites the user asks for, plus the official release pages of the tools above.
* `ext.i-call.me`: update check and an optional notice file.
* `api.i-call.me/showlef_users.php`: a usage signal at most once a day: a random install id, the program version, the language and the Windows version. Nothing else. See `UsersPing` in `app/Common.cs`.
* `api.i-call.me/showlef_login.php`: the **one-time Google sign-in**. The released program asks the user to sign in with Google once, before the first download: it opens this page in the browser with a random session id, the page sends the user to Google, and the program then asks the server, every 2 seconds, whether that session is done. Only the `openid email profile` scopes are requested, so the server receives the user's name and verified e-mail address (used to announce updates and to give support). No other Google data is read, and no browser file is read to find addresses (the program only looks at Chrome's profile folder, locally, to tell whether the extension is installed; nothing from it is sent).
* `api.i-call.me/showlef_feedback.php`: the message the user sends from the Contact window (and, if the user chooses, support information with paths anonymised).
* `www.gravatar.com`: the account picture shown next to the settings button (an MD5 hash of the e-mail address, as Gravatar requires); without a Gravatar the initial is shown.

## Code signing policy

Free code signing is provided by [SignPath.io](https://signpath.io), certificate by the
[SignPath Foundation](https://signpath.org). Only the release binary built from this repository by the GitHub Actions
workflow `release.yml` is submitted for signing; every release is approved by the maintainer below.

* Committer, reviewer and approver: [showf68](https://github.com/showf68)
* Privacy: see "What the program sends over the network" above. No other data leaves the PC.
