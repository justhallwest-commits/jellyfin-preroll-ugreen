# Jellyfin Preroll Ugreen: Pre-Roll Videos 3.2.1

Plays clips from a library you choose (studio logos, HallFlix bumpers, intros) before movies and TV episodes,
including every episode you pick and every episode that autoplays, in the browser and in the Jellyfin app on
Fire TV / Android TV. Builds for **Jellyfin 10.10**, **10.11** and **12**; the Jellyfin catalog picks the right one
for your server automatically.

## What's new in 3.2.1: Fire TV / Android TV app
The Jellyfin app on Fire TV doesn't use the web player, and it only asks the server for pre-rolls before **movies**.
For an episode it builds its own play list, so episodes never got a pre-roll there. Worse, the old server fallback
told the app to play the pre-roll while the episode was already playing, and the app closes its player when that
happens, so **neither the pre-roll nor the episode played**.
- **Pre-rolls in the app's episode list:** when the app asks for the episodes it's about to play, the plugin adds
  the planned pre-rolls to that list (*pre-roll → episode → pre-roll → next episode…*). The app plays them with its
  own player, autoplay included, with no flash and no remote control needed. Tags, genres, schedules, the frequency
  limit and skipped users all apply. An episode that resumes part-way stays first, so resume still works.
  **Play** and **Shuffle** on a whole season or show get the same treatment.
- **Server fallback is safe on the Fire TV app:** it never sends a play command while the app is playing anymore.
  It stops the episode, waits until the app says it stopped, then sends *pre-roll → episode → pre-roll → next…*
  starting from 0:00. If the app doesn't stop, or you press Play again in the meantime, the server leaves it alone.
  The fallback is only needed in a few cases (e.g. "play from beginning" on a half-watched episode); you'll see the
  episode close for a second before the pre-roll starts.
- **Fallback list kept in order:** the app looks up the fallback's play list with `/Items?ids=…`, and Jellyfin
  answered in its own order, so the app often played the episode before its pre-roll. The plugin keeps the order.
- **No interruptions mid-binge:** if the app's episode list left a pre-roll out on purpose (frequency limit), the
  server won't stop the app later to add one.
- **Activity is more honest:** "TV app queue" entries show what was added; the fallback now warns when an app
  restarts an episode without playing the pre-roll, instead of reporting success.
- Settings → General → "Jellyfin app on Fire TV / Android TV" (on by default), with a field for other app names.

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
2. **Catalog → Pre-Roll Videos → Install** the newest version offered (10.10 servers get 1.3x.x.0, 10.11 servers get 3.x.x.0, Jellyfin 12 gets 3.x.x.12).
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
- **Fire TV / Android TV app** (default on): pre-rolls are added to the episode list the app plays from (see above).
  Movies use the app's own pre-roll request, so keep **Settings → Playback → Prerolls** on in the app.
  The app's "Next up" screen after an episode shows the pre-roll's name and picture; give clips friendly names, or in
  the app set Settings → Playback → Next up → **Show next video info** to *Without thumbnail* or *Disabled*.
- **Server fallback** (default on): for apps without the web player (native TV apps), the server tells the app to play
  the pre-roll first when something starts without one. This needs an app that accepts remote control, and you may see a
  split-second flash. On the Fire TV / Android TV app it stops the episode first, then sends the pre-roll list.
- Resumed items keep resuming without a pre-roll. The episodes after them still get one.

## Releasing an update (GitHub website only)
**Actions → Build & release → Run workflow** → version like `3.2.1`.
One release with three zips (10.10, 10.11 and 12); `manifest.json` is updated automatically.
The 10.10 build gets version `1.<major×10+minor>.<patch>.0` (3.2.1 → 1.32.1.0) so newer servers never pick it.

## Troubleshooting
Start with the **Overview** tab; most problems show up there with a fix. Then check **Activity**, which shows what
happened on each play and why.

| Symptom | Fix |
|---|---|
| Settings page looks old after updating | Restart Jellyfin, then Ctrl+F5 (or try a private window). |
| Only some episodes get a pre-roll | Make sure "Web player integration" is on, and look in Activity for "Web player integration active". |
| A clip never plays | Clips tab: is it On, short enough, in season? Test tab shows why something else was picked. |
| Seasonal clip plays at the wrong time | Schedules tab → check the time zone. |
| Fire TV: movies get a pre-roll but episodes don't | Update to 3.2.1+, restart Jellyfin, and check Activity for "TV app queue" after playing an episode on the Fire TV. |
| Fire TV: pressing Play on an episode plays nothing / goes back to the episode page | Plugin older than 3.2.1: update. Its fallback sent a play command mid-playback, which closes the app's player. |
| Fire TV: no "TV app queue" entry when playing an episode | In the Jellyfin app: Settings → Playback → Next up → **Queue all episodes** must be on (otherwise the app never asks for an episode list, and only the fallback can help). |
| Fire TV: movies don't get a pre-roll | In the Jellyfin app: Settings → Playback → Prerolls → on. |
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
  NativeQueueFilter.cs                Fire TV / Android TV app: pre-rolls in its episode queue, ordered /Items?ids
  PrerollQueuePlanner.cs              plans pre-rolls along a whole play queue (schedules, frequency limit)
  WebPlayerInjection.cs               adds the script to the web app's index.html (in memory)
  Web/prerollClient.js                web player integration (queue building)
  Configuration/configPage.html       the settings page (script must stay inside the page div)
manifest.json                         Jellyfin plugin repository feed
```
