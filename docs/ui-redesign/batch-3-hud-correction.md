# Batch 3 correction — the match HUD is not the editor shell

The HUD boards (3.1 and 3.2) put the battlefield inside the editor shell: a framed viewport, a
control-group rail down the left and a full-height inspector on the right. That reads as an
editor or a replay tool, not a game. That's my brief's fault: I told you to keep the same shell
on every screen. For the live match, drop that rule.

Redo 3.1, 3.2 and 3.6 like this:
- **Full-bleed battlefield.** The 3D view runs edge to edge behind everything, with no frame or
  border ornament around it. At least about 80% of the screen is open battlefield.
- **Top strip**, about 40px: resources and supply, clock, game speed, and a small menu and alerts
  cluster on the right. Nothing else up there.
- **Bottom console**, no taller than about 22% of the screen, in the classic RTS layout (Warcraft
  III, StarCraft II, Age of Empires IV): minimap bottom-left, selection in the middle (portrait
  and stats, grouped portraits for big selections, the production queue, or a hero's level, XP
  and six-slot inventory), command card bottom-right. The console doesn't grow to fill empty
  space; no stat grid just to fill it.
- **Control groups:** a small row of tabs just above the selection area, showing only the groups
  that are assigned. No vertical rail.
- **Alerts, pings and chat** appear as overlays and fade away (above the minimap or top-centre).
  Nothing permanent over the battlefield.
- **Scenario custom UI zone:** draw nothing when the map doesn't use it. Show it only on 3.6,
  where the arena's kill scoreboard and respawn timer sit as overlays on the battlefield.
- **Ornament** lives on the console's top edge and corners only. Latency and fps are off by
  default (a setting), not on the HUD.
- **Battlefield art:** the near-photoreal terrain from my corrections (Manor Lords look) with
  real-looking units and buildings, so we can judge readability. No flat shapes.

Keep **3.4 Spectator and replay** with side panels: nobody is giving orders there, so a framed
view with every player's panels is right. Put a clear "Spectating" or "Replay" label at the top.
Pause and score screens can keep the shell.

Redo 3.1, 3.2 and 3.6 with this layout, then stop for review.
