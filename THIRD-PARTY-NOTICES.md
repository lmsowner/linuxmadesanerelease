# Third-Party Notices

Linux Made Sane includes third-party components that are not owned by Richard D. Kiernan and are not licensed under the Linux Made Sane Business Source License. Those components remain under their own upstream licenses.

This notice is intended to preserve attribution and make redistribution review practical. It does not change the license terms of any third-party component.

## Vendored Browser Assets

The following browser assets are vendored under `src/LinuxMadeSane.Web/wwwroot/lib`.

| Component | Local path | Upstream | License / notice |
| --- | --- | --- | --- |
| xterm.js | `wwwroot/lib/xterm` | `https://github.com/xtermjs/xterm.js` | MIT. Copyright notices are preserved in `xterm.css`; bundled JavaScript should be treated as xterm.js MIT assets. |
| xterm-addon-fit | `wwwroot/lib/xterm-addon-fit` | `https://github.com/xtermjs/xterm.js` | MIT. Distributed as part of the xterm.js project. |
| PDF.js | `wwwroot/lib/pdfjs` | `https://github.com/mozilla/pdf.js` | Apache License 2.0 for PDF.js files. Some annotation SVG assets carry Mozilla Public License 2.0 notices and those notices must be preserved. |
| UTIF.js | `wwwroot/lib/utif/UTIF.js` | `https://github.com/photopea/UTIF.js` | MIT. Copyright (c) 2017 Photopea. |
| hls.js | `wwwroot/lib/media-player/vendor/hls.min.js` | `https://github.com/video-dev/hls.js` | Apache License 2.0. |
| mpegts.js | `wwwroot/lib/media-player/vendor/mpegts.min.js` | `https://github.com/xqq/mpegts.js` | Apache License 2.0. |
| h265web.js | `wwwroot/lib/media-player/h265web` | `https://github.com/numberwolf/h265web.js` | CYL_Free-1.0 / upstream free usage agreement. This is a non-standard license. Review before using it in a paid redistribution, appliance, or customer-facing hosted product. |
| GitHub Invertocat mark | `wwwroot/images/brand/github-invertocat-black.svg` | `https://brand.github.com/foundations/logo` | GitHub, the GitHub logo design, Invertocat, Octocat, and related marks are trademarks of GitHub, Inc.; the Octocat design is the exclusive property of GitHub, Inc. Used only as a social link to the public GitHub project. |
| LinkedIn [in] logo | `wwwroot/images/brand/linkedin-in-bug.png` | `https://brand.linkedin.com/in-logo` | LinkedIn logo copyright LinkedIn Corporation. Used only as a hyperlink to the project owner's LinkedIn profile, subject to LinkedIn Brand and User Agreements. |
| h265web.js bundled dependencies | `wwwroot/lib/media-player/h265web` | h265web.js distribution | Includes bundled dependencies with embedded MIT and Apache-2.0 notices, including `es6-promise`, `m3u8-parser`, `mpd-parser`, `video.js`, `vtt.js`, `@videojs/http-streaming`, `aes-decrypter`, and `pkcs7`. Preserve embedded notices. |

## NuGet Packages

Top-level and runtime package dependencies are resolved by NuGet. The package license metadata should be checked during release preparation with:

```bash
dotnet list LinuxMadeSane.sln package --include-transitive
```

Known package license metadata at the time of this review:

| Package family | License |
| --- | --- |
| Microsoft ASP.NET Core, Entity Framework Core, Extensions, IdentityModel packages | MIT |
| Fido2 / Fido2.Models | MIT |
| QRCoder | MIT |
| OpenAI .NET SDK | MIT |
| SSH.NET | MIT |
| BouncyCastle.Cryptography | MIT |
| NSec.Cryptography | MIT |
| SQLitePCLRaw packages | Apache License 2.0 package metadata; SQLite itself is public-domain software where applicable |
| GtkSharp, AtkSharp, CairoSharp, GdkSharp, GioSharp, GLibSharp, PangoSharp | LGPL v2.0 package family used by the Desktop Assistant helper tray integration |
| Newtonsoft.Json | MIT |
| xUnit and .NET test packages | Test-only dependencies; retain package notices if redistributed in a test bundle |

## Desktop Runtime Packages

The Desktop Assistant helper uses GtkSharp for the Linux tray surface. GtkSharp is a .NET binding layer for GTK and related libraries. The native GTK, GLib, GDK, ATK, Cairo, and Pango runtime libraries are expected to come from the user's Linux distribution and are not owned by Linux Made Sane. Preserve upstream package notices and review distro package license metadata when redistributing a complete appliance image.

## Release Checklist

Before publishing source or binaries:

- preserve this file and the root `NOTICE`
- preserve upstream notices embedded in `wwwroot/lib`
- do not add Linux Made Sane BSL headers to third-party vendored files
- verify package license metadata for new NuGet dependencies
- legal-review any non-standard or copyleft dependency before including it in a public binary or source release

## Codec Notice

Some media playback paths can involve patented audio/video codecs, depending on browser, operating system, hardware, source media, and enabled player paths. This notice covers software copyright licenses only; it is not a patent license. Review codec patent obligations separately before shipping a commercial media playback appliance or hosted service.
