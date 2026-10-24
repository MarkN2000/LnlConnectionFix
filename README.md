# LnlConnectionFix

A [Resonite](https://resonite.com/) mod that fixes LNL connection handling when a connected user's IP address or port changes.

Prevents missed disconnects (including missing `UserLeft` events), dropped incoming messages, and stalled network statistics caused by failed connection lookups.

Install on the session host, including headless servers. Connecting users do not need this mod. No configuration required.

Pre-release: checked against Resonite 2026.9.18.82 with RML 5.0.1 on .NET 10. Live sessions and headless startup have not yet been tested.

## Installation

1. Install [ResoniteModLoader](https://github.com/resonite-modding-group/ResoniteModLoader).
2. Download `LnlConnectionFix.dll` from [Releases](https://github.com/MarkN2000/LnlConnectionFix/releases).
3. Place it in the host's `rml_mods` folder and restart Resonite.

## License

[MIT](LICENSE) © 2026 markn2000

[markn`s homepage](https://markn2000.com/posts/2025-09-08-resonite-mod-list/)
