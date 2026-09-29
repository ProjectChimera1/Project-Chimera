# Round 2 — the rest of Project Chimera's screens

I've approved Round 1 in this project: "Chimera Design System" and "Map Editor". They are now the
locked foundation. Use them to lay out every other screen in the game.

## Keep it, don't redesign it
- Use the Round 1 tokens, type, ornaments (`ornaments.js`), line icons (`icons.js`) and components
  exactly as they are. Reuse them; don't restyle them. If a screen really needs a new component
  (a star rating, a lobby slot, a node-graph wire, a card grid, a progress bar), add it to a new
  board, "04 · Components added in Round 2", on the design-system page in the same style, then
  use it from there.
- Keep the same shell: the top bar with Play / Create / Browse (F1 / F2 / F3), the left rail, a
  centre that is never covered, the right inspector, the bottom context strip and the Ctrl+K
  palette. Play and Browse fill the same zones differently, the way board 03 shows Create and
  the HUD doing it. No floating windows. Modal dialogs are for destructive confirmations only.
- The Round 1 rules still apply: 1920×1080 artboards; real hover, focus, selected and disabled
  states; hotkey glyphs and a tooltip on every control; a simple and an advanced mode in every
  editor; player colours only inside chips (Okabe-Ito with glyphs); WCAG AA contrast; UI scale
  from 80% to 150%; flat panels, borders and line icons only, so it can be built as native Unreal
  UI (no blur, no heavy live effects).
- Microcopy: an arcane touch is fine, stat names stay plain, as in Round 1. The bioluminescent
  accent stays at one per screen.

## Ornament and motion levers
Round 1 already has ornament levers in the tweaks panel (intensity 1–5, glow, corners, headers,
rail, buttons, minimap, texture, dividers). Grow that into a full lever panel I can use to switch
each effect on or off and turn it up or down, live, on every Round 2 screen.
- Every lever has an off switch and an intensity from 1 to 5. A master intensity scales them all.
- New ornament levers:
  - **Border style:** several to choose from, e.g. a single engraved line, a double line, a woven
    rope, filigree corners only, and a border studded with sigils.
  - **Gold rope border:** two thin gold lines twisting round each other along a panel's border,
    like a woven cord. Low intensity shows it only at the corners; high intensity runs it along
    the whole edge.
  - **Growth:** the rope and filigree slowly grow along the border when a panel opens, then rest.
  - **Sigils:** small transmutation symbols at panel corners, section headers and dividers. Choose
    the symbol set (circles, triangles, the seals from the reference images) and how many appear.
- New motion levers, each with an off switch plus intensity for speed and strength:
  - **Pulse:** a slow, breathing glow on the living accent (AI working, the active tool, a Ready
    or Start button).
  - **Shimmer:** a faint light that travels along the gold lines every few seconds.
  - **Turn:** transmutation circles (the minimap ring, loaders, the AI circle) rotate slowly.
  - **Draw-in:** borders, dividers and sigils trace themselves in when a screen or panel appears.
  - **Hover:** gold line-work brightens and the rope tightens under the cursor.
  - **Seal:** the "complete" array closes with a short stamp.
- Subtle by default: the starting setting is a quiet 2 out of 5. Motion never moves text or
  controls, never flashes, and never loops faster than about once every 2 seconds. In the match
  HUD it stays on the edges and alerts, never over the battlefield.
- The game's Reduced motion setting stops all motion and leaves the ornaments still. Add a player
  option too: UI ornament Off / Subtle / Full, under Settings (Graphics and Accessibility).
- Buildable in Unreal and cheap: opacity, colour, a mask sliding along a border, a rotating image or
  a material parameter. No blur, no particles. Add a one-line note for each saying how it would
  be built.
- Add a board, "05 · Ornament and motion", to the design-system page. Show each lever at off, 2 and
  5, with its animation playing.

## Corrections to the first brief
- Roles: players are **Commanders** and creators are **Architects**. Use those words wherever the
  UI names a role.
- Don't use "the Law of Equal Exchange" anywhere. It isn't ours to use. Say "the showcase
  campaign", or use "[World]" as a placeholder for the world's name. The Crucible Covenant
  (slate-blue) and the Sanguine Court (oxblood) are fine as working names.
- The world art now leans toward near-photoreal (like Manor Lords), with Northgard's stylized look
  as the fallback. Draw 3D backdrops as muted, detailed, realistic terrain, and show that the HUD
  stays readable over a busy, detailed battlefield.
- Custom games can be any genre, as in Warcraft III: RTS maps, tower defence, castle fights,
  DotA-style arenas, team survival, RPGs. Play and Browse must show that range, not only 1v1 maps.

## First: a screen map
Before any screens, make one board, "00 · Screen map". Show every screen below as a small box,
with arrows for how you get from one to another (the click and the hotkey) and where Esc goes back
to. Mark which shell zones each screen uses. Stop there so I can check it.

## Then five batches, in this order
Put each batch in its own file, with one artboard per screen or state. Stop after each batch so I
can review it before the next.

### Batch 1 — Front door (file: "Front Door")
1. **Title and loading.** Logo, "press any key", build version and online status. The loading
   screen uses the transmutation-circle progress with the map name, a tip and, for custom maps,
   the integrity check ("Verifying 214 files").
2. **Home (main menu).** The screen after the title. A slow, living 3D view of the world sits
   behind a menu anchored to one side: Continue (the last save or project, with a thumbnail),
   Campaign, Skirmish, Multiplayer, Custom Games, Create, Browse, Heroes, Settings, Quit. Each item
   has a hotkey. A side column holds recent games and projects, subscribed content with updates
   waiting, and one featured scenario. Show the profile chip with login state. States: first
   launch (no saves, plus an invitation to "Build your first map in 15 minutes"), a returning
   Commander, and offline.
3. **Play hub (F1).** Campaign (the three-mission prologue: briefing, difficulty, locked and
   completed states), Skirmish, Multiplayer (quick match, a list of open lobbies, join by code,
   party), Custom Games (installed scenarios filtered by genre and player count, with a Play
   button that starts straight away), and Load game / Replays (save slots with thumbnails and an
   autosave marker; replays with duration and players).
4. **Skirmish setup.** A map picker with a minimap preview and player count. Two to four slots,
   each with Commander or AI, AI personality (four presets) and difficulty, faction, colour and
   glyph, and team. Rules: starting resources, fog on or off, game speed. Then Start. States: a
   valid setup, and a slot conflict that blocks Start and says why.
5. **Multiplayer lobby.** Host and guest views. Slots show ready state, ping, faction, colour and
   glyph, and team, with spectator slots. It has chat, the host's settings, kick and a countdown.
   A per-player "content matches" check, with "Update required" and a one-click download that
   shows progress. States: all ready, one player missing content, one player downloading, the
   host has left.
6. **Settings.** Built in the shell, not as a window: categories in the left rail, the form in the
   centre, a description and live preview on the right. Categories: Gameplay; Graphics (presets
   and the frame-rate cost of each); Audio; Controls (remappable keys with conflict warnings and
   separate Play and Create keymaps); Accessibility (UI scale 80–150% with a live preview, a
   colourblind check of the player colours, subtitles, reduced motion, text size); AI providers
   (Claude, OpenAI Codex, OpenRouter or a local Ollama model, with your own key, a connection
   test, and a clear note that everything works with AI off); Account.

### Batch 2 — Browse and publish (file: "Browse")
1. **Browse home (F3).** Search, genre tags, sort (trending, top rated, newest, most played) and
   filters (player count, map size, has AI opponents, genre). A grid of scenario cards, each with
   thumbnail, name, Architect, genre, players, rating and subscribed state. Content-type tabs:
   Scenarios, Factions, Units and abilities, Models.
2. **Detail page.** A gallery, the description, the Architect and their other work, ratings and
   reviews, a version history, size and required content, and a "verified" integrity badge.
   Actions: Subscribe, Play, Open in Create (when remixing is allowed), Report. States: not
   subscribed, downloading with progress, installed, update available, verification failed.
3. **My library.** Subscribed, installed and updates waiting, with disk use and unsubscribe.
4. **Publish flow** (from Create, Ctrl+Shift+P), inside the shell. A checklist: the validator
   passes ("Array stable"); proof of play (you must beat your own scenario, shown with your last
   clear); thumbnail, description and screenshots (all required); genre and tags; a rights
   attestation for imported assets; visibility; version notes; upload progress; and a published
   confirmation. States: blocked because the map hasn't been beaten, validation errors with
   "jump to field", uploading, published.

### Batch 3 — In the match (file: "Match")
1. **HUD.** Everything from the first brief (resources and supply, clock, minimap with fog of war,
   selection with portraits, command card with hotkey glyphs, costs, progress and "needs X"
   locks, control groups 1 to 9, alerts). Add: a hero with level, XP and a six-slot inventory; a
   production queue five deep; rally points; pings; game speed; the zone where a scenario's own
   custom UI sits.
2. **HUD states.** Early game; a big battle with more than 12 units selected (portraits grouped
   by type); a hero selected; a shop building selected (buy items); a building selected with
   production and research queued; an "Under attack" alert; "Waiting for player" with a drop
   countdown; chat and pings.
3. **Pause menu** (resume, settings, save, load, surrender, quit) and the **score screen** for
   victory and defeat, with tabs for overview, economy, army and a timeline graph.
4. **Spectator and replay view.** Every player's resources, no command card, a player-vision
   switch; for replays, a timeline scrubber and playback speed.
5. **Hero picker** (before a match that uses persistent heroes). Saved heroes with a portrait in
   the specimen vat, level, signature ability, carried items and currency.
6. **Custom-genre proof.** One HUD for a DotA-style arena scenario whose custom widgets (a kill
   scoreboard and a respawn timer) come from the Game UI builder. It should show that custom UI
   fits the shell.

### Batch 4 — Create, part 1 (file: "Editors 1")
1. **Create home (F2).** Projects with thumbnail, last edited, version and published state. New
   project from a map template, a faction, a unit pack, or a remix from Browse. The entry point
   to the guided "first map in 15 minutes" path.
2. **Unit card.** Everything in one card: the model in the specimen vat, stats as sliders with
   typed values, costs, abilities, tags, promote to hero. A simple / advanced toggle, a raw-data
   view, templates, compare with another unit, and the "complete" seal. States: a new unit from a
   template, a validation error at the field, and compare.
3. **Item card and building card**, in the same pattern. Items: charges and stat bundles. Buildings:
   costs in any resource, production, research, and working as a shop.
4. **Ability editor.** Presets, then effect blocks that snap together (pay 30 health → find 8
   nearby allies → heal them), and passives (auras, on-hit effects). The AI box shows its work:
   type "a healing prayer that's stronger the more wounded the target is", and it rewrites that
   into the real fields, highlights which words became which setting, and offers confirm, reroll
   or variants. States: presets, the block graph, an AI draft waiting for review.

The top bar already has tabs for Map, Units, Abilities, Rules, Tech tree and Game UI. Items,
Buildings, Factions and Imports need a home too. Propose where they go, but keep it one bar.

### Batch 5 — Create, part 2 (file: "Editors 2")
1. **Rules editor.** One logic model shown four ways: presets, WHEN / IF / THEN sentences built
   from menus, a node graph, and plain English through the AI. Regions and units picked in the
   map are linked, not typed.
2. **Rules debugger** during a playtest: live variables, a log of what fired, and a click that
   jumps to the rule.
3. **Tech tree editor.** Drag wires between nodes; a loop is rejected when you drop it, and the
   UI says why.
4. **Faction wizard.** Five steps, finishing in 12 minutes or less, ending in "Play against the
   AI".
5. **Game UI builder.** Drag widgets onto a 16:9 canvas that shows the HUD's safe zones, bind them
   to rule variables, and make buttons fire game events.
6. **Import manager.** Bring in your own models, images and sounds. Show the triangle and texture
   caps and whether each file is within them, plus the rights attestation.

## For every batch
- Start each file with a small index board listing its artboards.
- Label what is a static mock and what is interactive.
- Reuse Round 1 components; list any new ones on board 04.
- Show real content from the showcase (Crucible Covenant and Sanguine Court units, "Ashfall Ford"
  and other maps), not lorem ipsum.
