# Submarine — Plex-Style In-Playback Subtitle Search, Download & Multi-Format Engine for Jellyfin

**Submarine** (`Jellyfin.Plugin.Submarine`) is a Jellyfin 10.9+ / 10.10+ (`.NET 8.0`) server plugin that brings Plex's on-the-fly **in-playback subtitle search, download, and multi-format switching** directly into the playback subtitle menu across **all Jellyfin platforms**.

---

## Dual-Layer Cross-Platform Architecture

### 1. Universal Virtual Subtitle Streams (Works on 100% of Clients: Roku, Android TV, Apple TV / Swiftfin, Kodi, iOS, Android, Web)
Native TV and mobile clients (Roku BrightScript, Android TV Kotlin/ExoPlayer, Apple TV Swiftfin) render their playback subtitle selector directly from the `MediaStream` tracks returned by `POST /Items/{itemId}/PlaybackInfo`.

* [`PlaybackInfoSubtitleFilter`](Jellyfin.Plugin.Submarine/Filters/PlaybackInfoSubtitleFilter.cs) intercepts `PlaybackInfo` responses across all clients and queries [`SubmarineSubtitleService`](Jellyfin.Plugin.Submarine/Services/SubmarineSubtitleService.cs).
* Submarine injects **Virtual On-Demand Subtitle `MediaStream` Tracks** (`Index >= 10000`, `DeliveryMethod = SubtitleDeliveryMethod.External`) for your configured preferred languages and formats (`SRT`, `VTT`, `ASS`, `SSA`, `SUB`, `TTML`):
  - `⬇ [Submarine • OpenSubtitles] ENG (SRT) - Movie.2026.1080p.BluRay ★Match`
  - `⬇ [Submarine • SubDL] ENG (VTT) - Movie.2026.WEB-DL [HI]`
* When a user selects any `⬇ [Submarine ...]` track from the native playback subtitle menu on Roku, Android TV, Swiftfin, or Kodi, the client requests Submarine's `DeliveryUrl` (`/Plugins/Submarine/Stream/{itemId}/{virtualIndex}/Stream.{format}`).
* Submarine downloads the remote subtitle on the fly, converts it into the target format via [`SubtitleFormatConverter`](Jellyfin.Plugin.Submarine/Services/SubtitleFormatConverter.cs), streams it immediately to the active video player without stopping playback, and optionally saves it permanently to your Jellyfin media library.

### 2. Interactive Plex-Style In-Player Search Modal (`jellyfin-web`, Desktop JMP, Mobile Apps, LG WebOS, Samsung Tizen)
* [`SubmarineWebInjectionMiddleware`](Jellyfin.Plugin.Submarine/Web/SubmarineWebInjectionMiddleware.cs) automatically injects [`submarine-player.js`](Jellyfin.Plugin.Submarine/Web/submarine-player.js) and [`submarine-player.css`](Jellyfin.Plugin.Submarine/Web/submarine-player.css) into `jellyfin-web`.
* During video playback, Submarine hooks into the player's **Subtitles (`CC`) menu** and adds:
  - **`🔍 Search & Download Subtitles (Submarine)...`** inside the subtitle action sheet
  - A quick-access Submarine search icon button in the video player OSD
* Clicking it opens a dark-glassmorphic **Plex-style Subtitle Search & Download Modal** directly over the video player with:
  - Language selector (`eng`, `spa`, `fre`, `ger`, `jpn`, etc.)
  - Custom release/title search query filter
  - Target subtitle format pills (`SRT`, `VTT`, `ASS`, `SSA`, `SUB`, `TTML`)
  - Live millisecond timing offset adjustment (`offsetMs`)
  - Instant live `<track>` injection into the active HTML5 `<video>` element without reloading or stopping playback.

---

## Multi-Format Subtitle Conversion Engine

[`SubtitleFormatConverter`](Jellyfin.Plugin.Submarine/Services/SubtitleFormatConverter.cs) is a zero-dependency C# parser and serializer supporting bidirectional conversion, tag translation (`<i>`/`<b>`/`<u>` ↔ `{\i1}`/`{\b1}`/`{\u1}`), and timestamp offset shifting across:
* **SRT** (`SubRip` — `.srt`)
* **VTT** (`WebVTT` — `.vtt`)
* **ASS** (`Advanced SubStation Alpha` — `.ass`)
* **SSA** (`SubStation Alpha` — `.ssa`)
* **SUB** (`MicroDVD` `{startFrame}{endFrame}` & `SubViewer` — `.sub`)
* **TTML / DFXP** (`Timed Text Markup Language` — `.ttml`, `.dfxp`)

---

## Building & Testing

```powershell
# Build Release DLL
dotnet build Jellyfin.Plugin.Submarine.sln -c Release

# Run the xUnit Test Suite (25 unit & integration tests)
dotnet test Jellyfin.Plugin.Submarine.sln -c Release
```
