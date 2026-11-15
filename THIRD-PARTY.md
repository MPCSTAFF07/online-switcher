# Third-party components

This package contains compiled code and redistributable runtime files from:

- Microsoft .NET / ASP.NET Core / Windows Desktop Runtime 8.0 — MIT and accompanying third-party notices. Runtime license and notices are retained in sender/ and host/.
- SIPSorcery 10.0.17 and its NuGet dependencies — see package metadata and upstream licenses: https://github.com/sipsorcery-org/sipsorcery
- NAudio 2.2.1 — MIT: https://github.com/naudio/NAudio

FFmpeg is NOT bundled. The setup script installs it separately through the Windows Package Manager. FFmpeg build licenses and notices depend on the installed distribution. See https://ffmpeg.org/legal.html and the installed package's license files.

STUN is configured by default to stun.l.google.com:19302. No TURN service, subscription, domain registration, or hosting is included.

Source projects pin their top-level NuGet dependencies. Restore with `dotnet restore`; NuGet supplies transitive package license metadata.
