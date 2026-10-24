# Jellyfin Preroll Ugreen: Pre-Roll Videos

Plays clips from a library you choose (studio logos, HallFlix bumpers, intros) before movies and/or TV episodes.
It's a normal server-side Jellyfin plugin with builds for **Jellyfin 10.10** and **10.11**. The Jellyfin catalog picks the right build for your server automatically.

## Features
- Pick a pre-roll library, 1–5 clips per play, shuffle (no back-to-back repeats) or sequential order, max clip length
- Separate on/off switches for movies and TV episodes
- **A pre-roll before every movie and every episode, including autoplay.**
  - *Why this is needed:* Jellyfin's player only asks for pre-rolls when playback starts at the first item of its queue.
    Pressing Play on an episode queues the whole series and starts partway in, so on its own Jellyfin only plays a
    pre-roll before S01E01, and never before autoplayed episodes.
  - **Web player integration** (default on): the plugin adds a small script to the Jellyfin web app (in memory, nothing
    written to disk). When you press Play it builds the queue as *pre-roll → episode → pre-roll → next episode…* for
    browsers, Jellyfin Media Player, the Android phone app and WebView TV apps. There's no flash, and the audio and
    subtitle tracks you picked still apply to the episode.
  - **Server fallback** (default on): for apps that don't use the web player, when a movie or episode starts without a
    pre-roll, the server tells the app to play the pre-roll first. This only works on apps that accept remote control,
    and you may see a split second of the episode first.
  - Resumed items keep resuming (no pre-roll in front of a resume), but the episodes after them still get one.
- **Recent activity** on the settings page shows, for every play, what was queued, played or skipped and why.
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
1. After installing or updating, **restart Jellyfin**, then **reload** every open Jellyfin tab or app so it loads the new script.
2. Play any episode (not S01E01) from the beginning. Expect pre-roll → episode. Skip to near the end and the next episode
   should get its own pre-roll.
3. Dashboard → Plugins → Pre-Roll Videos → **Recent activity** → **Refresh**. You should see
   "Web player integration active", "Queued … before it" and "✓ Pre-roll played first".

## Releasing an update (GitHub website only)
**Actions → Build & Release Plugin → Run workflow** → type a version like `2.4.0`.
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
| Only the first episode gets a pre-roll | Update to 2.3.0 or later, restart Jellyfin, reload the app/tab. Recent activity should show "Web player integration active". |
| No "Web player integration active" line | The app isn't loading the script: reload it (Ctrl+F5 in a browser), check "Web player integration" is on. Native apps (Android TV/Fire TV, Roku, Swiftfin) don't use the web player; they rely on the server fallback. |
| "✗ this app doesn't accept remote play commands" | That native app can't be told to play the pre-roll. Use a web-based app on that device if one exists. |
| "pre-roll clips could not be loaded for this user" | Give that user access to the pre-roll library (Dashboard → Users → Access). |

## Files
```
.github/workflows/build-release.yml   test build on every commit; Run workflow = release + manifest update
JellyfinPreroll/                      plugin source (net8.0 for 10.10, net9.0 for 10.11)
  Plugin.cs                           GUID a4b2c3d4-e5f6-7890-abcd-ef1234567891
  PluginConfiguration.cs              settings incl. tag rules
  PluginServiceRegistrar.cs
  PrerollSelector.cs                  clip selection, tag matching, exclusions, loop protection
  PrerollIntroProvider.cs             pre-rolls when an app asks for intros
  PrerollSessionWatcher.cs            server fallback + per-play activity
  PrerollController.cs                API: web script, queue plan, activity
  PrerollActivityLog.cs               "Recent activity" buffer
  WebPlayerInjection.cs               adds the script to the web app's index.html (in memory)
  Web/prerollClient.js                web player integration (queue building)
  Configuration/configPage.html       Dashboard settings page (script must stay inside the page div)
manifest.json                         Jellyfin plugin repository feed
```
