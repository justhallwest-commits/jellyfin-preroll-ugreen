# Jellyfin Preroll Ugreen: Pre-Roll Videos 3.0

Plays clips from a library you choose (studio logos, HallFlix bumpers, intros) before movies and TV episodes,
including every episode you pick and every episode that autoplays. Builds for **Jellyfin 10.10** and **10.11**;
the Jellyfin catalog picks the right one for your server automatically.

## What's new in 3.0
- **New settings page with tabs:** Overview · General · Matching · Schedules · Clips · Test · Activity
- **Overview health check:** finds problems and fixes them for you:
  - users who can't see the pre-roll library → **Give them access** button
  - clip tags that match nothing (typos like "Horor")
  - rules and schedules that point at tags no clip has
  - clips that are too long, and devices where the web player script hasn't loaded
- **Create the pre-roll library from the plugin**, with a folder browser (handy for UGREEN container paths)
- **Smarter matching, no manual tagging needed:** a clip tagged `Horror` plays before anything with the *genre* Horror.
  Optional matching on studios/networks (`HBO`, `Pixar`) and content ratings (`PG`, `TV-MA`)
- **Rules on any detail:** Tag, Genre, Studio, Rating, Library, Title or Type → clip tag
  (e.g. *Library = Kids Movies → KidsIntro*, *Type = Movie → MovieBumper*)
- **Schedules:** seasonal and time-based pre-rolls with one-click presets (Halloween, Thanksgiving,
  Christmas, New Year, Valentine's, Fourth of July, Weekend nights, Late night).
  Choose whether they *replace* general clips, *mix in*, or *play before everything*. Runs in your own time zone
- **Cinema-style sequence:** *Logo → matched clip → Feature Presentation*, with steps for movies only or episodes only
- **How often:** before every item, or at most once every 15 min / 30 min / 1 h / … per viewer (great for binge-watching)
- **Skip users** (kids' profiles, or yourself while testing)
- **Clips manager:** thumbnails, durations, add/remove tags inline, switch clips on/off, play counts and last played
- **Test tool:** search any movie or episode and see exactly which pre-roll would play and why. You can pick a
  viewer and a date/time to test schedules ("what plays on Dec 10?")
- **Backup:** export/import all settings as a file; libraries are matched by name on another server
- Your 2.x settings carry over automatically (old tag rules move into the new rules list)

## Install / update (Jellyfin dashboard)
1. Dashboard → **Plugins → Repositories → +**
   URL: `https://raw.githubusercontent.com/justhallwest-commits/jellyfin-preroll-ugreen/main/manifest.json`
2. **Catalog → Pre-Roll Videos → Install** the newest version offered (10.10 servers get 1.30.0.0, 10.11 servers get 3.0.0.0).
3. **Restart Jellyfin**, then reload Jellyfin in your browser (Ctrl+F5) and reopen apps.
4. Open **Dashboard → Plugins → Pre-Roll Videos** and follow the **Overview** tab.

## Quick setup
1. Put your clips in a folder Jellyfin can see (MP4/H.264 works best).
2. Overview → **Create a pre-roll library** → Browse to the folder → Create. Or pick an existing library on General.
3. **Clips tab:** leave a few clips untagged as general pre-rolls, and tag the others (`Horror`, `Christmas`, `Logo`…).
4. **Test tab:** look up a movie or episode to confirm what will play.

## How delivery works
- **Web player integration** (default on): a small script added to the Jellyfin web app builds the play queue as
  *pre-roll → episode → pre-roll → next episode…*. Covers browsers, Jellyfin Media Player, the Android phone app
  and WebView TV apps. There's no flash, and your audio and subtitle choices still apply.
- **Server fallback** (default on): for apps without the web player (native TV apps), the server tells the app to play
  the pre-roll first when something starts without one. This needs an app that accepts remote control, and you may see a
  split-second flash.
- Resumed items keep resuming without a pre-roll. The episodes after them still get one.

## Releasing an update (GitHub website only)
**Actions → Build & Release Plugin → Run workflow** → version like `3.1.0`.
One release with two zips (10.11 and 10.10); `manifest.json` is updated automatically.
The 10.10 build gets version `1.<major×10+minor>.<patch>.0` (3.1.0 → 1.31.0.0) so a 10.11 server never picks it.

## Troubleshooting
Start with the **Overview** tab; most problems show up there with a fix. Then check **Activity**, which shows what
happened on each play and why.

| Symptom | Fix |
|---|---|
| Settings page looks old after updating | Restart Jellyfin, then Ctrl+F5 (or try a private window). |
| Only some episodes get a pre-roll | Make sure "Web player integration" is on, and look in Activity for "Web player integration active". |
| A clip never plays | Clips tab: is it On, short enough, in season? Test tab shows why something else was picked. |
| Seasonal clip plays at the wrong time | Schedules tab → check the time zone. |
| "✗ this app doesn't accept remote play commands" | That native app can't be told to play a pre-roll; use a web-based app on that device if possible. |
| Plugin shows **Malfunctioned** | Dashboard → Logs, search `JellyfinPreroll`; usually the wrong build for the server version. |

## Files
```
.github/workflows/build-release.yml   test build on every commit; Run workflow = release + manifest update
JellyfinPreroll/                      plugin source (net8.0 for 10.10, net9.0 for 10.11)
  Plugin.cs                           GUID a4b2c3d4-e5f6-7890-abcd-ef1234567891
  PluginConfiguration.cs              all settings (rules, schedules, sequence, users, frequency…)
  PrerollSelector.cs                  the matching / scheduling engine
  PrerollController.cs                API: web script, queue plan, health, clips, test, activity
  PrerollStats.cs                     play counts (saved) + per-viewer frequency tracking
  PrerollActivityLog.cs               "Activity" tab buffer
  PrerollIntroProvider.cs             pre-rolls when an app asks for intros
  PrerollSessionWatcher.cs            server fallback + per-play activity
  WebPlayerInjection.cs               adds the script to the web app's index.html (in memory)
  Web/prerollClient.js                web player integration (queue building)
  Configuration/configPage.html       the settings page (script must stay inside the page div)
manifest.json                         Jellyfin plugin repository feed
```
