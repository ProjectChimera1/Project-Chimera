# Mod.io Setup Guide — Project Chimera

## What You Need Before Starting
- Your **API key** (from mod.io → your game page → Admin → API Access)
- Your **mod.io game URL** (e.g. `https://api.mod.io/v1/games/12345`)
- The **Game ID integer** — this is the number at the end of your game URL, e.g. `12345`

---

## Step 1 — Wire Your Credentials Into the Godot Inspector

The game reads these from two `[Export]` fields on the `MainScene` node. You set them once in the editor; they are **not** hardcoded.

1. Open the Godot editor.
2. In the Scene panel, click the root **MainScene** node.
3. In the **Inspector** (right panel) scroll down to the section labelled **Ugc** (or search for "mod io").
4. Set **Mod Io Game Id** → the integer from your game URL (e.g. `12345`).
5. Set **Mod Io Api Key** → your API key string (the long alphanumeric key from mod.io).
6. Save the scene (`Ctrl+S`).

> The `ModIoService.cs` class reads these values and uses them to construct every REST call to `https://api.mod.io/v1/games/{gameId}/mods`. No other files need changing.

---

## Step 2 — Create a Game on Mod.io (if not done yet)

If you haven't already created a game entry on mod.io:

1. Go to [https://mod.io](https://mod.io) and log in.
2. Click **Add a game** (top right).
3. Fill in your game name, description, and upload a logo.
4. Set **Visibility** to "Hidden" while you're testing — you can publish later.
5. Go to **Admin → API Access** on your game page.
6. Click **Generate Key** — this is your API key.
7. Note the numeric Game ID from the URL: `https://mod.io/g/your-game-name` shows the ID in the API URLs.

---

## Step 3 — Get Your Game ID Numerically

The mod.io web URL shows a slug (e.g. `/project-chimera`), but the API needs the integer ID.

**Find it:**
1. On your game's mod.io page, click **Admin → API Access**.
2. Look at the **REST API** section — it shows example URLs like:
   ```
   GET https://api.mod.io/v1/games/12345/mods
   ```
   The `12345` is your Game ID. Copy that number.

---

## Step 4 — Test the Connection In-Game

1. Press **F5** in Godot to run the project.
2. Press **O** to open the Content Browser.
3. Click the **Online** tab.
4. If your Game ID and API key are correct, you'll see a "Browse" button that fetches mods from mod.io.
5. Click **Browse** — it sends:
   ```
   GET https://api.mod.io/v1/games/{gameId}/mods?api_key={yourKey}
   ```
   and populates the card list with any published mods for your game.

**If you see nothing / an error:**
- Check the Godot Output panel for `[ModIo]` log lines.
- Verify the Game ID is the integer (not the slug string).
- Verify the API key has no extra spaces.
- Make sure your game visibility is at least "Hidden" (fully private games block API access).

---

## Step 5 — Upload Your First Test Mod

Mod.io doesn't have an in-game upload button yet (that's a future feature). Upload manually for now:

1. In the Godot editor, use the **Export** button in the Creation Suite toolbar to export a `.chmr` or content package file.
2. Go to your game's mod.io page → **Submit a Mod**.
3. Fill in the mod name, description, tags.
4. Upload your exported file as the mod's main file.
5. Set visibility to **Public** or **Hidden** as needed.
6. The mod will now appear in the in-game Online browser when players click Browse.

---

## Step 6 — Download Flow (In-Game)

When a player clicks a mod card in the Online browser:

1. The game calls `ModIoService.GetDownloadUrl(modId)` to fetch the download link.
2. It calls `OS.ShellOpen(downloadUrl)` — this opens the download in the **system browser** (this is the current implementation; a native in-game downloader is a planned Phase 5 enhancement).
3. The player downloads the file manually and places it in their local mods folder.

The **author name** shown on each card is a clickable link that opens the mod author's mod.io profile in the system browser.

---

## Quick Reference — Key Values

| Field | Where to find it | Example |
|---|---|---|
| Game ID (integer) | `api.mod.io/v1/games/**12345**/mods` in API docs | `12345` |
| API Key | Game admin → API Access → Generate Key | `eyJhbGciOiJ...` |
| Inspector field | MainScene → Inspector → Ugc → Mod Io Game Id / Mod Io Api Key | |
| REST base URL | Auto-constructed by `ModIoService.cs` | `https://api.mod.io/v1` |

---

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| Online tab shows nothing | Wrong Game ID or API key | Double-check Inspector values |
| `[ModIo] HTTP error 401` | API key invalid or expired | Regenerate key on mod.io |
| `[ModIo] HTTP error 403` | Game is fully private | Set game visibility to Hidden or Public |
| `[ModIo] HTTP error 404` | Game ID wrong | Check the number in the API URL |
| Cards appear but download fails | `OS.ShellOpen` blocked by OS | Try running the game as admin, or test URL in browser |

---

## Source Code Reference

- `godot/src/UGC/ModIoService.cs` — REST client (get mods list, download URL)
- `godot/src/UI/ContentBrowserPanel.cs` — UI (Local | Online tabs, card rendering)
- `MainScene.cs` — `[Export] public int ModIoGameId` and `[Export] public string ModIoApiKey`
