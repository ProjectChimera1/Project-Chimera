> ⚠️ DEPRECATED 2026-04-16 — Content migrated to vault knowledge base at 20_Reference/GameDev/godot-csharp/LEARNINGS.md (auto-injected each session). Archived at 30_Archive/Chimera_LEARNINGS_archived_2026-04-16.md. Do not update this file.

# Project Chimera — Godot & C# Learnings

## How This File Works
This is an auto-growing reference built from real session experience. Every /save, Claude Code appends what it learned. On /start, Claude reads this to avoid repeating mistakes.

---

## Godot 4.6 API — Confirmed Working Patterns

- `MultiMesh.SetInstanceTransform(i, Transform3D.Identity.Scaled(Vector3.Zero))` — hides an instance by zeroing its scale (dead/unused slots)
- `MultiMesh.TransformFormat = MultiMesh.TransformFormatEnum.Transform3D` — must be set before assigning `InstanceCount`
- `Engine.GetFramesPerSecond()` — reliable FPS counter for debug labels; call in `_Process`
- `new CanvasLayer()` + `AddChild(label)` — correct way to render a `Label` on screen in a 3D scene
- `WorldEnvironment` + `new Godot.Environment()` — ambient light requires both; set `AmbientLightSource`, `AmbientLightColor`, `AmbientLightEnergy` on the Environment resource
- `Mathf.DegToRad(angle)` — use for setting `Rotation` on nodes in radians
- `[Export] public YourEnum Prop { get; set; } = YourEnum.Default;` on `partial class : Node3D` — exports any public enum as an Inspector dropdown

## Godot 4.6 API — Known Pitfalls & Fixes

- **`project/assembly_name` must match `<AssemblyName>` in .csproj** — mismatch → "cannot instantiate C# script, class not found". Fix: both to `ProjectChimera`.
- **Godot editor rewrites project.godot** — always re-Read before editing it in the same session.
- **`Key` enum uses mixed case**: bracket keys are `Key.Bracketleft` / `Key.Bracketright` (NOT `BracketLeft`/`BracketRight`). CS0117 is the tell.
- **`float.IsInf()` does not exist in C#** — use `float.IsInfinity(x)`. `float.IsNaN(x)` is correct.
- **Godot 4.6 C# NavigationMesh property names**: `GeometryParsedGeometryType` and `GeometrySourceGeometryMode` (NOT `ParsedGeometryType`/`SourceGeometryMode` — those are enum TYPE names and cause CS0572/CS0118).

## C# in Godot — Patterns That Work

- `public partial class Foo : Node3D` — `partial` required on ALL Godot-derived classes.
- **SoA entity storage with free list**: `int[] _freeList` + `int _freeCount` gives O(1) create/destroy with no allocation per tick.
- **Counting-sort spatial hash rebuild** (3 O(n) passes: count → prefix-sum → insert) — allocation-free each tick; safe at 1000+ entities.
- **`[MethodImpl(MethodImplOptions.AggressiveInlining)]`** on FixedPoint arithmetic — eliminates call overhead on hot math.
- **`long` intermediate for Fixed multiply**: `(int)(((long)a.Raw * b.Raw) >> FRACTIONAL_BITS)` — avoids int overflow.
- **Fixed timestep accumulator**: clamp `realDelta` to 0.25s max to prevent spiral-of-death after breakpoints.
- **`ISimSystem` + `params ISimSystem[] systems`** — clean simulation pipeline composition without reflection.

## C# in Godot — Gotchas & Workarounds

- **`using Godot;` forbidden in simulation layer** — only `src/UI/` and scene scripts touch Godot types. `FixedVec3.ToGodotVector3()` bridge method is an acceptable exception.
- **`#nullable enable` must be at the top of every file using `?` annotations** — omitting it causes CS8632 warnings.

## Build & Compile — Solutions to Errors We've Hit

- **"Cannot instantiate C# script: associated class not found"** — root cause: `project/assembly_name="godot"` vs `ProjectChimera.dll`. Fix: `<AssemblyName>ProjectChimera</AssemblyName>` in csproj AND `project/assembly_name="ProjectChimera"` in project.godot.
- **Edit tool "file modified since read"** — Godot editor rewrites project.godot on open; always re-Read before editing.

## Camera & Raycasting — Confirmed Patterns

- `Camera3D.ProjectRayOrigin(pos)` + `ProjectRayNormal(pos)` — get ray from screen pixel; hit Y=0 plane via `t = -origin.Y / dir.Y`, then `hit = origin + dir * t`.
- `Camera3D.UnprojectPosition(worldPos)` → screen coords — position 2D overlays above 3D units. Always check `IsPositionBehind()` first.
- **RTS camera rig**: Node3D pivot on ground, Camera3D child at `(0, dist*sin(pitch), dist*cos(pitch))`, `_camera.LookAt(GlobalPosition, Vector3.Up)` each frame. `RotateY()` on rig for yaw; pan moves rig.
- `GlobalTransform.Basis.Z` = world backward — use `-Basis.Z` for WASD forward after yaw.
- **`[Export] bool` toggle + keyboard flip**: declare on node, flip in `_UnhandledInput` with `SetInputAsHandled()`, show live state in HUD.

## Shaders, Materials & Overlays — Confirmed Patterns

- Inline GLSL: `var s = new Shader(); s.Code = "shader_type spatial; ..."; var mat = new ShaderMaterial(); mat.Shader = s;` — no .gdshader file needed.
- `BaseMaterial3D.ShadingModeEnum.Unshaded` + `EmissionEnabled = true; Emission = color * multiplier` — glowing indicators that ignore scene lighting.
- **Valid transparent overlay render modes**: `blend_mix, depth_draw_never, cull_disabled, unshaded`. `diffuse_unshaded` is INVALID (runtime shader error). `specular_disabled`/`diffuse_lambert` are lighting model options — omit with `unshaded`.
- `filter_nearest` on a fog texture uniform — keeps cell boundaries sharp (no bilinear blur between states).

## Dynamic GPU Textures — Confirmed Pattern

- `Image.CreateFromData(w, h, false, Image.Format.R8, byteArray)` + `ImageTexture.CreateFromImage(image)` once; then `_texture.Update(_image)` each frame after modifying `_image` in-place.
- `ShaderMaterial.SetShaderParameter("fog_texture", _texture)` — bind ImageTexture to a sampler2D uniform.

## Scene Architecture — Confirmed Patterns

- `GetParent().AddChild(node3d)` in `_Ready()` — safe way to add a 3D sibling from a non-Node3D child node.
- **Singleton via static Instance on a scene child** (not autoload): `public static GameState Instance { get; private set; }` set in `_Ready()`.

## NavigationServer3D — Confirmed Patterns

- `NavigationServer3D.MapGetPath(GetWorld3D().NavigationMap, start, end, optimize: true)` → `Vector3[]`; index 0 is start; `MapForceUpdate` is obsolete (CS0618) — server updates automatically between frames.
- **PathRequestSystem pattern**: Presentation-layer Node draining up to 30 nav queries/frame; advances per-entity waypoint queue, writes `world.MoveTarget[id]`; sim layer (MovementSystem) is unchanged.
- **Waypoint reach radius = 1.5u** (squared check): tighter → jitter; 1.5u forgiving without skipping corners.
- **Geometry-baked navmesh for dynamic obstacles**: `GeometryParsedGeometryType = StaticColliders`, `GeometrySourceGeometryMode = RootNodeChildren`, `CellSize=1.0`, `AgentMaxClimb=0.25`. Add ground + building `StaticBody3D` as children of `NavigationRegion3D`. Call `BakeNavigationMesh(false)` after geometry changes (synchronous, fine for rare events).
- **NavObstacleManager pattern**: Per-building `StaticBody3D?[]`; `_Process` detects adds/removes, sets `_dirty`; rebakes once per frame if dirty. `public void MarkDirty() => _dirty = true` allows external triggers (e.g. TerrainBrush after sculpting).
- **Building bodies on NavigationRegion3D, not MainScene**: With `RootNodeChildren` mode, only region children are scanned.
- **Always Duplicate navmesh before baking**: `(NavigationMesh)template.Duplicate()!` — bake into copy, assign back; forces Godot to re-register the region.

## UI Panels & CanvasLayer — Confirmed Patterns

- `Button.Pressed += OnMyMethod;` — C# event subscription for dynamically created buttons; works after `AddChild`.
- `panel.MouseFilter = Control.MouseFilterEnum.Stop;` — makes panel consume all mouse events, preventing click-through to 3D scene's `_UnhandledInput`.
- **Command card pattern**: poll `SelectionSystem.SelectedBuildingId` each `_Process` frame — zero coupling from sim layer.
- `button.Disabled = true` + updated `button.Text` — gray out with reason (e.g. `"[need ore]"`).
- **`_Input` vs `_UnhandledInput` for input priority**: `_Input` fires before `_UnhandledInput` and before UI; call `GetViewport().SetInputAsHandled()` to consume events. Use `_Input` when a system must intercept events before other nodes (e.g. TerrainBrush consuming LMB while active). Use `_UnhandledInput` for lower-priority toggles that shouldn't interfere.
- **`panel.GetGlobalRect().HasPoint(screenPos)`** — check if a screen-space mouse position is over a panel before raycasting into 3D; prevents terrain paint / unit selection from firing when the user clicks inside a UI card.
- **`anchorRoot` pattern for CanvasLayer full-screen input blocking**: `var anchorRoot = new Control(); anchorRoot.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect); anchorRoot.MouseFilter = Control.MouseFilterEnum.Stop; canvas.AddChild(anchorRoot);` — direct CanvasLayer children don't reliably cover the full viewport; this intermediate Control does.
- **Lambda loop capture**: `var bType = WORKER_BUILD_TYPES[i]; btn.Pressed += () => OnBtnPressed(bType);` — always copy the loop variable to a local before capturing in a lambda; without this all buttons invoke with the last iteration's value.
- **Worker construction pattern**: `UnitCommand.Build` + `BuildTarget[]` int SoA; `GatheringSystem` skips workers where `CommandState==Build`; `BuildingSystem.TickWorkerArrival()` uses `SqrDistance` proximity check (3u²) to clear the command and set `GatherState=Idle`. Building construction ticks autonomously — worker arrival is just "done walking".
- **TerrainBrush slider live-update**: call `ApplyBrushSettings()` inside `ContinuePaint()` (every `operate()`) not only in `BeginPaint()` — otherwise mid-stroke size/strength slider changes have no effect until the next stroke.

## Data-Driven Unit Archetypes — Confirmed Patterns

- **`GetUnitByCategory`**: `foreach` + `string.Equals(u.Category, cat, OrdinalIgnoreCase)` — returns first match; JSON ordering determines which unit a building trains.
- **Building type → unit category as a static switch**: Define once in `BuildingSystem.CategoryForBuilding()`. Both `TrainUnit()` and `SpawnTrainedUnit()` call the same helper.
- **`GetProductionUnit(BuildingType)`** on `BuildingSystem` — lets `CommandCardSystem` read unit name/cost/time without holding a `FactionDefinition` reference.
- **`#nullable enable` per-file** (not project-wide) — avoids CS8618 flood on Godot Node classes that use `_Ready()` init.

## Godot UI — ButtonGroup & Toggle Patterns

- **Single-selection button row**: `var g = new ButtonGroup(); btn.ToggleMode = true; btn.ButtonGroup = g;` — activating one button auto-deactivates the rest; no manual state management needed.
- **Setting `ButtonPressed` programmatically**: fires `Toggled` signal but NOT `Pressed` — safe to sync palette state from keyboard handlers without recursive callbacks.
- **`Button.Toggled` vs `Button.Pressed`**: use `Toggled += (bool on) => { ... }` when you need the new boolean state; use `Pressed` when you just need "clicked" (no state passed). Snap toggles should use `Toggled`.
- **Clearing a container's children synchronously**: `RemoveChild(child)` + `child.QueueFree()` — removes from scene tree immediately so `GetChildren()` is clean before adding new children. `QueueFree()` alone leaves children in the tree until end-of-frame.
- **`GetViewport().GetVisibleRect().Size.X`** — safe to call from `Initialize()` as long as the node is already in the scene tree (i.e., after `AddChild(node)` by the parent).
- **Full-screen overlay on CanvasLayer**: `control.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect)` — fills the entire viewport. Works on `ColorRect`, `Label`, or any `Control` as a direct child of a `CanvasLayer`. Add overlay children last so they render on top.
- **Anchor-based right-edge pinning for panels**: `panel.AnchorLeft = 1f; panel.AnchorRight = 1f; panel.OffsetLeft = -panelWidth - margin; panel.OffsetRight = -margin; panel.GrowHorizontal = Control.GrowDirection.Begin;` — keeps the panel flush to the right viewport edge regardless of resolution. Never use absolute `Position` for panels that must stay in a corner.
- **`HFlowContainer` for wrapping button rows**: Use `HFlowContainer` instead of `HBoxContainer` when the number of buttons may exceed panel width (e.g. unit type palette). `HFlowContainer` wraps children; `HBoxContainer` clips them.
- **GameState.ModeChanged` signal in C#**: `_gameState.ModeChanged += (int mode) => { ... }` — `mode` is `(int)GameMode.Edit` (0) or `(int)GameMode.Play` (1). Use to show/hide Edit-only panels without polling each frame.
- **Sharing a CanvasLayer across setup methods**: store as a field (`_uiCanvas`) rather than a local var — lets `SetupHud()`, `SetupWinConditionUi()`, `SetupGameOverOverlay()` all add children to the same layer.
- **Grace period before win check**: `_playFrames` counter incremented in `_Process`; guard `> 180` (~3s at 60fps) before calling `CheckWinCondition()` — prevents instant-win when a faction starts with 0 entities before scenario applies.

## Multi-Faction Data — Confirmed Patterns

- **Per-faction `FactionDefinition?[]` in simulation systems**: `private readonly FactionDefinition?[] _factions = new FactionDefinition?[5];` indexed by `(int)Faction`. Helper `GetFactionDef(Faction f)` centralises bounds-check. `SetFactionDef(Faction, FactionDefinition)` allows runtime override from scenario loading — avoids re-constructing the system mid-game.
- **`ActiveFactionDef()` helper on `EntityPlacer`**: `return _mode == PlacementMode.P2Unit ? _faction2 : _faction;` — single call point for all spawn methods; keeps P1 vs P2 placement paths identical.
- **Scenario-driven faction loading**: `ApplyScenario` reads `slot.FactionJson`, calls `ProjectSettings.GlobalizePath()` + `File.Exists()` guard, then `FactionDefinition.LoadFromFile()` + `_buildSys.SetFactionDef(faction, def)` — enables same-faction mirrors (alpha vs alpha, beta vs beta) without code changes.
- **`dotnet build` is authoritative — use it to catch errors early**: `dotnet build godot.csproj` compiles against the NuGet `Godot.NET.Sdk` which closely matches the editor's runtime. All errors must be fixed before the DLL is regenerated at `.godot/mono/temp/bin/Debug/ProjectChimera.dll`. The editor's MCP `run` action does NOT trigger a rebuild — it uses whatever DLL exists on disk. A stale DLL means UI changes are invisible even after code edits.
- **`ENetPacketPeer.FlagReliable` is `long`; `Send` takes `int` flags**: Call as `peer.Send(channel, payload, (int)ENetPacketPeer.FlagReliable)`. The `Flags` enum type does NOT exist — CS0426. Parameter type must be `long flags`, then cast to `int` at the call site.
- **`ENetConnection.ConnectToHost(ip, port, channels)`** — NOT `ConnectTo`. CS1061 if you use the old name.
- **`SubViewport.CanvasItemDefaultTextureFilter = Viewport.DefaultCanvasItemTextureFilter.Nearest`** — replaces the old `Canvas2DTexSmooth = false`. CS0117 if you use the old property.
- **`RichTextLabel.ScrollFollowing = true`** — NOT `ScrollFollowingEnabled`. CS0117 if you use the old name.
- **`Control.LayoutPreset.Center`** — NOT `CenterCenter`. CS0117. The full list includes `TopLeft, TopRight, BottomLeft, BottomRight, CenterLeft, CenterTop, CenterRight, CenterBottom, Center, LeftWide, ...`
- **`AddThemeColorOverride` cannot be called inside an object initializer**: C# object initializers only allow property/field assignments, not method calls. Move any `AddTheme*Override(...)` calls to after the object is constructed. CS0747 if you try.

## Deterministic Lockstep Networking — Confirmed Patterns

- **FNV-1a 32-bit checksum over Fixed raw ints** — feed each int as 4 bytes LE; never use float math in the hash path. Hash entity positions + health (ascending ID), building alive+health, faction ore. Fire every 60 ticks via `SimulationLoop.OnChecksum` delegate.
- **`SimulationLoop.StepOnce()`** — advance exactly one tick bypassing the time accumulator. Used in online mode where tick advancement is gated on both peers' commands arriving, not wall clock. `Update(realDelta)` remains for offline free-running.
- **`LockstepManager.Flush(tick)` pattern**: sends local commands on first call for a tick (`IsStalling` guard prevents double-send), polls transport, returns `true` only when remote commands for that tick have arrived. `MainScene._Process` calls `if (_lockstep.Flush(tick)) _simLoop.StepOnce()` when online.
- **Command interception via `EnqueueOrder()` return value**: `EnqueueOrder()` returns `true` = apply now (offline pass-through); `false` = queued, caller must NOT apply. Zero branching in call sites: `if (!EnqueueCommand(id, cmd, dest)) continue;`.
- **Path-request bridge delegates on LockstepManager**: `OnRequestPath/OnRequestAttackMove/OnCancelPath` — `Action<int,float,float>?` wired in `MainScene.OnMatchStart()`. Keeps `LockstepManager` pure C# while still triggering Godot's NavServer. Remote peer's commands get paths requested identically to local commands.
- **ENetConnection Godot 4.6 C# pattern**: `Service(0)` returns `Godot.Collections.Array`; index [0] = `(ENetConnection.EventType)(int)result[0]`, [1] = `result[1].As<ENetPacketPeer>()`, [2] = `result[2].AsByteArray()`, [3] = `(int)result[3]`. Use `peer.Send(channel, payload, ENetPacketPeer.FlagReliable)`. `CreateHostBound("*", port, maxPeers:1, maxChannels:2)` for server.
- **Known Phase 2 lockstep limitation**: NavServer3D path results are not deterministic across machines. Units may take slightly different routes → position divergence → checksum mismatch after ~30s. Flow fields (Phase 3) fix this by replacing NavServer with a sim-layer deterministic pathfinder.

## Minimap & SubViewport — Confirmed Patterns

- **`SubViewport.World3D = GetViewport().World3D`** — share the main scene's geometry with a SubViewport for minimap / picture-in-picture. Wire in `_Ready()` (not `Initialize()`) to ensure tree is settled. Both cameras now see the same entities without duplicating scene nodes.
- **`SubViewportContainer.StretchShrink = 1`** — display SubViewport at 1:1 pixel scale inside a fixed-size container. Set container `Size` explicitly; `SubViewport.Size` must match.
- **Orthographic top-down camera**: `Camera3D { Projection = Orthogonal, Size = mapDiameter, Position = (0,200,0), RotationDegrees = (-90,0,0) }` — covers the full map in one shot. `Size` is world-unit width, not pixel width.
- **`Control._GuiInput(InputEvent)` for minimap click-to-pan**: fires when `MouseFilter = Stop`; handles both `InputEventMouseButton` (press) and `InputEventMouseMotion` with `ButtonMask.Left` (drag). Call `AcceptEvent()` to consume so clicks don't bleed to the 3D scene.
- **Anchoring a Control to a screen corner**: `SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight)` then `OffsetRight = -margin; OffsetBottom = -margin` — gives a fixed-size panel with a gap from the corner.
- **`public new FieldType? FieldName`** — use the `new` keyword to suppress CS0108 when a nested class field shadows a `Node` property (e.g. `public new MinimapBridge? Owner` shadowing `Node.Owner`).

## Streaming Textures — Confirmed Patterns

- **RGBA8 streaming texture for per-pixel alpha**: allocate `byte[] _data = new byte[w * h * 4]` once; write R/G/B/A per pixel each frame; call `_image.SetData(w, h, false, Image.Format.Rgba8, _data)` + `_texture.Update(_image)`. Identical to R8 pattern in FogOfWarBridge but with 4 bytes/pixel for alpha control.
- **`Time.GetTicksMsec()` for match duration**: record `_matchStartMs = Time.GetTicksMsec()` on first play frame; on win, `elapsed = GetTicksMsec() - _matchStartMs`; format as `$"{elapsed/1000/60}:{elapsed/1000%60:D2}"`.
- **`Fixed.ToInt()`** — `Raw >> FRACTIONAL_BITS`, truncates toward zero. Correct for converting accumulated ore amounts to display integers (vs `ToFloat()` which preserves fractional part).

## Flow Fields — Confirmed Patterns

- **Building-change polling in bridge**: store `bool[] _prevAlive = new bool[BuildingStore.MAX_BUILDINGS]` + `int _prevCount`; each `_Process` frame compare vs `_buildings.Alive[]`; call `_flowSys.RebuildObstacles(_buildings)` on any diff. Mirrors `NavObstacleManager` — handles place, destroy, and editor undo/redo without explicit callbacks.
- **Drop-in PathRequestSystem replacement**: change `PathRequestSystem?` field/param to `FlowFieldBridge?` in `SelectionSystem` — identical `RequestPath/RequestAttackMove/CancelPath` API; zero other changes needed.
- **Initialization order**: create `FlowFieldSystem` + `FlowFieldBridge` in `SetupNavigation()`; call `flowFieldSys.RebuildObstacles(buildings)` + `flowFieldBridge.Initialize(world, flowSys, buildings)` AFTER `LoadAndApplyScenario()` so obstacle map is seeded with all placed buildings.

## mod.io REST API (System.Net.Http) — Confirmed Patterns

- **`HttpClient` in Godot .NET 8**: standard `System.Net.Http.HttpClient` works without any NuGet package. Single instance stored as a field (not created per request). Add `User-Agent` header in constructor.
- **`ConcurrentQueue<Action>` + `DrainEvents()`**: same pattern as NakamaService — fire `Task.Run(async () => { ... _queue.Enqueue(() => event?.Invoke()); })` for all async ops; call `DrainEvents()` once per frame from `_Process`. Zero thread-safety issues.
- **Streaming download with progress**: `GetAsync(url, HttpCompletionOption.ResponseHeadersRead)` → `ReadAsStreamAsync()` → read in 80KB chunks; compute `read / total` for progress ratio. Wrap in `File.Create()` to stream directly to disk.
- **`FormUrlEncodedContent` with `KeyValuePair<string,string>[]`**: standard way to POST form data. The `new[]` array literal works because the element type is inferred. Wrap in `var payload = new FormUrlEncodedContent(new[] { new KeyValuePair<string,string>("key","val") })`.
- **`MultipartFormDataContent` for file upload**: `form.Add(new StreamContent(fileStream), "filedata", fileName)` for the binary part; `form.Add(new StringContent("value"), "fieldname")` for text fields. Assign to `req.Content`.
- **Bearer auth**: `req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token)` on each request that needs OAuth2.
- **`JsonDocument.Parse(body)` for one-off fields**: cleaner than defining a full class when you only need one or two values (e.g., extracting `access_token` or `username` from an auth response).
- **CS0841 forward-reference in closures**: if two buttons' closures reference each other (thumbUp disables thumbDown and vice-versa), declare BOTH variables before subscribing either event — `Button a = new(); Button b = new();` then wire handlers. The compiler sees the forward declaration.

## Settings Persistence — Confirmed Patterns

- **`SettingsManager` as a Node singleton**: `AddChild(_settingsMgr)` in `_Ready`; `static Instance` set in `_Ready`. Loads JSON on start, fires `OnSettingsChanged` event when applied. Other nodes subscribe rather than polling.
- **`AudioServer.GetBusIndex(name)`**: returns `-1` if bus doesn't exist. Always guard with `if (idx < 0) return` — buses are configured in the Godot Project Settings audio tab and may not exist during unit tests or headless runs.
- **`Mathf.LinearToDb(linear)`**: converts 0–1 volume slider to dB for `AudioServer.SetBusVolumeDb`. Hard-mute at 0: `float db = linear > 0 ? Mathf.LinearToDb(linear) : -80f` + `AudioServer.SetBusMute(idx, linear == 0)`.
- **Settings propagation on init**: call `_camCtrl.PanSpeedMultiplier = settings.CameraSpeed` in `SetupCamera()` (after `_settingsMgr` exists, before `AddChild(_camCtrl)`) so the first frame already uses saved values. Subscribe `OnSettingsChanged` for live updates.
- **`LinkButton` for clickable URLs**: `var btn = new LinkButton { Text = "by Alec", TooltipText = url }; btn.Pressed += () => OS.ShellOpen(url);` — opens system browser. Falls back to `Label` when URL is empty to avoid a button with no action.

## Main Menu / CanvasLayer Overlay — Confirmed Patterns

- **Full-screen menu as CanvasLayer layer=20**: sits above all game UI (HUD=8, content browser=10, settings=15). No separate scene needed — built programmatically in `Initialize()` and `AddChild`-ed to MainScene.
- **Dismiss on choice, not on Escape**: menu buttons close the overlay by setting `Visible = false`, then fire an event. Escape is reserved for Settings. Prevents accidental dismissal.
- **`StyleBoxFlat` hover state for primary button**: `btn.AddThemeStyleboxOverride("hover", ...)` with a lighter `BgColor` gives proper visual feedback without a theme asset. `"normal"` and `"hover"` are the two key state names for `Button`.

## Audio System — Confirmed Patterns

- **`AudioStreamPlayer` pool (round-robin)**: create `POOL_SIZE=8` players in `_Ready`, set `player.Bus = "SFX"`, advance `_poolIdx = (_poolIdx + 1) % POOL_SIZE` before each play. Assign `Stream` + call `Play()` — reuses nodes, zero per-frame allocation.
- **Optional asset loading**: `if (!ResourceLoader.Exists(path)) return null;` before `ResourceLoader.Load<AudioStream>(path)` — silently skips absent files so the framework works before art assets exist.
- **Pitch variation for combat SFX**: `player.PitchScale = PITCH_BASE + (float)GD.RandRange(-PITCH_VAR, PITCH_VAR)` (PITCH_VAR=0.08f) — prevents repetitive click sound during rapid melee. Disable for UI sounds and one-shots.
- **Do NOT clear CombatEventQueue in AudioManager**: `CombatFeedbackBridge` owns `_events.Clear()` at end of its `_Process`. AudioManager reads the same queue the same frame — just read, never clear.
