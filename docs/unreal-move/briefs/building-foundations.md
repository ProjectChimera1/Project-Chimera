# Brief: building foundations on editable terrain (map editor)

2026-10-01. Source: Alec, in the trial session ("when we do place a building on land, we want that land to be flat and then curved
down around the edges ... it would be more intuitive if, when we went to raise the land where a building was, it just raised a
chunk of land around the building with the building").

## Outcome
In the Unreal map editor, a placed building always stands on a flat foundation pad that blends smoothly into the terrain, and
sculpting never tears or buries a building.
- **Place:** the footprint (plus a small margin) levels to one height (the footprint's median ground height). A falloff skirt
  of a few metres blends the pad edge into the surrounding terrain with a smooth curve, not a cliff. If levelling would need more
  than a set height change across the footprint (too steep, e.g. a mountainside), the ghost turns red with "too steep" and the
  building is not placed.
- **Sculpt near a building:** a raise or lower stroke that touches a footprint moves the whole pad and the building together,
  rigidly, by the brush's change at the pad; the skirt re-blends. Smooth and flatten skip pads. Paint is unaffected.
- **Undo/redo:** one step restores the terrain and the building's height together.

## Why
Players and Architects expect buildings to sit cleanly on the land (Manor Lords and WC3 both do). Terrain editing that cuts
through buildings, or buildings floating on slopes, looks broken and makes maps unusable.

## Constraints
- **Map editor and custom-game creation only (Alec, 2026-10-01): terrain is never sculpted inside a match.** All sculpting
  happens while creating maps or custom games. The edited heights are content: the sim reads them when play starts (plan C §3.9),
  so nothing changes in the sim's rules. In a match, buildings are placed on the terrain as authored (placement rules only).
- Uses the existing terrain data core (brushes, undo, dirty rects, collision, scatter refresh) in ChimeraTerrain; deterministic
  results (same edits give the same heights); the footprint comes from the building's definition (the sim's footprint data).
- Text first; no Blueprints.

## Proof of done
- Scripted test: place a building on a 15° slope, then the pad is flat within 1 cm over the footprint, the skirt has no step
  above a set slope, and collision and the pick agree with the mesh; on a 45° slope the placement is refused.
- Scripted test: a raise stroke clipping one corner lifts the whole pad and the building by the same amount; undo restores both
  byte-exact (height hash).
- Screenshot pair for Alec: building on a hillside before and after a nearby sculpt.

## Out of scope
Any terrain change during a match (never planned); walls, roads and other spline structures; terraces or retaining walls.
