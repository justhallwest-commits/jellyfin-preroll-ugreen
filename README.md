# Jellyfin Preroll Ugreen: Pre-Roll Videos

Plays clips from a library you choose (studio logos, HallFlix bumpers, intros) before movies and/or TV episodes.
It's a normal server-side Jellyfin plugin with builds for **Jellyfin 10.10** and **10.11**. The Jellyfin catalog picks the right build for your server automatically.

## Features
- Pick a pre-roll library, 1–5 clips per play, shuffle (no back-to-back repeats) or sequential order, max clip length
- Separate on/off switches for movies and TV episodes
- **Pre-roll before every episode, including autoplay.** Jellyfin apps only ask for pre-rolls when you press Play.
  When the next episode starts by itself (autoplay, Up Next, Next button), or on apps that never ask, the plugin tells the
  device to play *pre-roll → that episode → the rest of the series*. It won't double up when the app already played a pre-roll,
  and it skips resumed playback. You may see a split second of the episode before the pre-roll takes over. Toggle:
  "Also play before every autoplayed episode".
- **Tag matching**
  - Same tag names match automatically: a clip tagged `Horror` plays before media tagged `Horror` (case doesn't matter)
  - Shows pass their tags down to every season and episode
  - **Tag rules** for when names differ: media tag `Marvel` → clips tagged `MarvelIntro`
  - Clips with no tags are your *general* pre-rolls
  - Choose what happens when nothing matches: general clips, any clip, or nothing
  - Leftover slots can be filled with general clips
  - **Skip tags**: media tagged e.g. `NoPreroll` never gets a pre-roll
- Skip whole libraries (Kids, Home Videos…)
- Loop protection: clips in the pre-roll library never get a pre-roll themselves

## Install (Jellyfin dashboard, no terminal)
1. Dashboard → **Plugins → Repositories → +**
   URL: `https://raw.githubusercontent.com/justhallwest-commits/jellyfin-preroll-ugreen/main/manifest.json`
2. **Catalog → Pre-Roll Videos → Install** (the newest version that fits your server is chosen).
3. Restart Jellyfin (Dashboard → Restart, or UGOS Docker → Containers → jellyfin → Restart).

## Setup
1. Put your clips in a folder Jellyfin can see, e.g. `Media/Prerolls`. MP4/H.264 works best on every client.
2. Dashboard → **Libraries → Add Media Library**: type *Home Videos and Photos* (or Movies), name `Prerolls`, metadata downloaders off.
3. **Tag the clips** you want matched: open a clip → ⋮ → **Edit metadata** → **Tags** → add e.g. `Horror` → Save.
   Leave general clips untagged.
4. Make sure your movies and shows have matching tags. Tags on a **show** count for all of its episodes.
5. Dashboard → **Plugins → Pre-Roll Videos**: pick the library, set your options and rules, then **Save**.
   The page lists the tags it found on your clips so you can check the spelling.

## Test
Start an item **from the beginning** in a web browser. Then go to Dashboard → **Logs**, open the newest log, and search `Pre-Roll Videos`. Each play is logged with the clips chosen and why, for example
`playing 1 pre-roll(s) before "Halloween" [matched tags: Horror]: Spooky`.
- Resuming never plays pre-rolls.
- Each client app decides whether to request intros. Web and most official apps do, but test each device you use.

## Releasing an update (GitHub website only)
**Actions → Build & Release Plugin → Run workflow** → type a version like `2.2.0`.
One release is created with two zips (10.11 and 10.10), and `manifest.json` is updated automatically.
The 10.10 build gets version `1.<major×10+minor>.<patch>.0` (e.g. 2.2.0 → 1.22.0.0) so a 10.11 server never picks it.

## Troubleshooting
| Symptom | Fix |
|---|---|
| Not in Catalog | Dashboard → Scheduled Tasks → *Update Plugins* ▶, then Ctrl+F5. Check `manifest.json` loads in a browser. |
| Plugin shows **Malfunctioned** | Dashboard → Logs, search `JellyfinPreroll`. Most likely the wrong build for the server version. |
| Checksum error on install | The zip attached to the release doesn't match `manifest.json`. Re-upload the exact file. |
| Library list empty | Refresh the page once the library scan finishes. Any error is shown in orange at the top. |
| Tagged clip never plays | Check the spelling against the "Tags on your pre-roll clips" list, and that the movie or show really has that tag. |
| Only the first episode gets a pre-roll | Update to 2.2.0 or later and keep "Also play before every autoplayed episode" on. |
| A device never gets autoplay pre-rolls | It doesn't accept remote control. The log says "can't be remote-controlled". |

## Files
```
.github/workflows/build-release.yml   test build on every commit; Run workflow = release + manifest update
JellyfinPreroll/                      plugin source (net8.0 for 10.10, net9.0 for 10.11)
  Plugin.cs                           GUID a4b2c3d4-e5f6-7890-abcd-ef1234567891
  PluginConfiguration.cs              settings incl. tag rules
  PluginServiceRegistrar.cs
  PrerollSelector.cs                  clip selection, tag matching, exclusions, loop protection
  PrerollIntroProvider.cs             pre-rolls when Play is pressed
  PrerollSessionWatcher.cs            pre-rolls for autoplayed episodes / apps that skip intros
  Configuration/configPage.html       Dashboard settings page (script must stay inside the page div)
manifest.json                         Jellyfin plugin repository feed
```
