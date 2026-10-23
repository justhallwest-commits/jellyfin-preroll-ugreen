# Jellyfin Preroll Ugreen — Pre-Roll Videos v2.0.0

Plays clips from a library you choose (studio logos, HallFlix bumpers, intros) before movies and/or TV episodes.
It's a normal server-side Jellyfin plugin, so it runs the same on a UGREEN NAS as on any other Jellyfin 10.11 server.

**What's new in 2.0.0**
- Skip-these-libraries checkboxes (e.g. Kids, Home Videos)
- Order: Shuffle (won't repeat the clip that just played) or Sequential (rotates A→Z)
- Loop protection: anything inside the pre-roll library never gets a pre-roll itself
- Music-video items count as pre-rolls too
- Fixed CI build: .NET 9 for Jellyfin 10.11, and one plugin GUID everywhere so your old settings carry over
- Releasing is one button: the workflow builds, publishes the release, and updates `manifest.json` with the checksum

**Requires:** Jellyfin **10.11.x** (Dashboard → About shows the version).

---

## Part 1: Set up this repository (web UI only)

This repo already includes a pre-built release, so you don't need to build anything to install.

1. Upload everything in this folder (`.github`, `JellyfinPreroll`, `manifest.json`, `README.md`) with **Add file → Upload files**, and commit to `main`.
2. **Releases → Draft a new release** → tag `v2.0.0` (**Create new tag**) → title `v2.0.0`.
   Attach `jellyfin-preroll-v2.0.0.zip` with the name unchanged → **Publish release**.

## Part 2: Future updates (optional)

Edit the code on GitHub, then go to **Actions → Build & Release Plugin → Run workflow** and enter a new version such as `2.0.1`.
The workflow builds the plugin on GitHub, publishes the release, and updates `manifest.json` for you.

---

## Part 3: Install on the UGREEN NAS

### Step 0: Check your Jellyfin version
Open Jellyfin (App Center → Jellyfin, or `http://<NAS-IP>:<port>`) → **Dashboard → About**.
- **10.11.x**: continue.
- **10.10 or older**: the plugin won't load. In UGOS, update Jellyfin from App Center. If App Center doesn't offer 10.11 yet, run the official `jellyfin/jellyfin` image yourself in **Docker → Project → Create** (UGREEN's Docker Compose method).

### Step 1: Make a pre-roll folder Jellyfin can see
The UGREEN Jellyfin app only sees the folders you gave it as **resource access paths** during install.
- Easiest: create a `Prerolls` folder **inside a media folder Jellyfin already uses**, e.g. `Media/Prerolls`.
- Copy your clips in (MP4/H.264 works best on every client; keep them short).

### Step 2: Create the pre-roll library
Dashboard → **Libraries → Add Media Library**
- Content type: **Home Videos and Photos** (or Movies)
- Display name: `Prerolls`
- Folder: the Prerolls folder from Step 1 (as Jellyfin sees it, e.g. `/media/Prerolls`)
- Turn off the metadata downloaders so it doesn't try to match logos to films.
- Let the scan finish.

> Keep each user's **library access** to Prerolls turned on, or their clients may refuse to play the clips.
> To keep it off the home screen, hide it per profile under **Settings → Home**.

### Step 3: Install the plugin

#### Method A — Plugin repository (recommended, no file access needed)
1. Dashboard → **Plugins → Repositories** (gear / "Manage repositories") → **+**
2. Name: `HallFlix`
   URL: `https://raw.githubusercontent.com/justhallwest-commits/jellyfin-preroll-ugreen/main/manifest.json`
3. **Plugins → Catalog** → **Pre-Roll Videos** → choose **2.0.0.0** → **Install**.
4. Restart Jellyfin: Dashboard → **Restart**, or in UGOS **Docker → Containers → jellyfin → Restart**.

Future versions then show up as updates in the Jellyfin plugin page automatically.

#### Method B — Manual DLL drop
Use this only if you set a **plugin path** when you installed Jellyfin from App Center.
The default config folder lives in a hidden `@appstore` area that UGOS Files doesn't show.
1. Download `jellyfin-preroll-v2.0.0.zip` from the GitHub release and unzip it to get `JellyfinPreroll.dll`.
2. In UGOS **Docker → Containers → jellyfin → Storage/Volumes**, find which NAS folder is mounted to Jellyfin's plugin folder (`/config/plugins`).
3. In that NAS folder, create a folder named `Pre-Roll Videos_2.0.0.0` and copy `JellyfinPreroll.dll` into it.
4. Restart the Jellyfin container.

### Step 4: Configure
Dashboard → **Plugins → Pre-Roll Videos**
- Pre-roll library: **Prerolls**
- Clips per play, Order, Max clip length
- Before movies / Before TV episodes
- Skip these libraries: check any libraries that should never get pre-rolls
- **Save**

### Step 5: Test
Start a movie **from the beginning** in a web browser. The clip should play first.
- Pre-rolls only play on a fresh start, never when resuming.
- Each client app decides whether to ask the server for intros. Web and most official apps do, but test each device you use (Fire TV, Roku, phones).
- Dashboard → **Logs** → open the newest log and search `Pre-Roll Videos` to see which clips were picked or why one was skipped.

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Plugin listed as **NotSupported / Malfunctioned** | Jellyfin isn't 10.11.x. Update it (Step 0). |
| Not in Catalog | Make sure Part 2 finished and `manifest.json` on GitHub lists 2.0.0.0. Then Dashboard → Scheduled Tasks → *Update Plugins*, or restart. |
| Checksum error on install | Run the workflow again with the same version. It rebuilds the zip and rewrites the checksum. |
| Library dropdown empty | Refresh the page after the library scan finishes. |
| Log says "has no usable videos" | The library scan hasn't finished, or Max clip length is filtering everything out. |
| Clips play on web but not on one TV app | That app doesn't request intros. It's a client limitation, not the plugin. |

## Files
```
.github/workflows/build-release.yml   test build on every commit; Run workflow = release + manifest update
JellyfinPreroll/                      plugin source (net9.0, Jellyfin 10.11)
  Plugin.cs                           GUID a4b2c3d4-e5f6-7890-abcd-ef1234567891
  PluginConfiguration.cs
  PluginServiceRegistrar.cs
  PrerollIntroProvider.cs             picks clips, exclusions, loop protection
  Configuration/configPage.html       Dashboard settings page
manifest.json                         Jellyfin plugin repository feed
```
