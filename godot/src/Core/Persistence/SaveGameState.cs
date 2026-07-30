#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ProjectChimera.AI;               // AiOpponentSystem
using ProjectChimera.Combat;           // ProjectileStore
using ProjectChimera.Core.Definitions; // FactionDefinition, UnitDefinition
using ProjectChimera.Core.Sim;         // SimulationHost
using ProjectChimera.Dsl;              // DslVarTable / DslLoopState / DslEventQueue
using ProjectChimera.Effects;          // ModifierStore, Modifier, PersistentEffect, EffectCaps

namespace ProjectChimera.Core.Persistence
{
    /// <summary>
    /// Story 11.3 (FR-67) — the in-memory capture/restore CORE of an SP mid-match save: every mutable sim store off
    /// <see cref="SimulationHost"/>, stored as its integer / <c>Fixed.Raw</c> / enum-as-int representation (no float
    /// ever, no object-graph walking). <see cref="CaptureFrom"/> snapshots a live host; <see cref="RestoreInto"/>
    /// OVERLAYS the snapshot onto a host that the load path has already scenario-re-applied (the
    /// <c>HeroProfileLoader.LoadInto</c> pattern lifted to the whole world) — a FULL REPLACE of the mutable arrays, not
    /// a merge, because the saved match's entity population / free list / high-water mark differ from the fresh
    /// scenario. Derived/authored/transient state (ModifierSystem accumulators, fog/pathability/elevation grids,
    /// compiled trigger IR + regions + win config, per-tick queues, PrevPosition, presentation copies) is NOT captured
    /// — the scenario re-apply + first tick re-derive it.
    ///
    /// <para>Reference-typed SoA (<c>EntityWorld.SourceDefinition</c>/<c>FeedbackProfile</c>, <c>HeroStore.SourceDef</c>)
    /// round-trips by the def's stable string id, re-resolved on load through the slot faction definitions (the
    /// <c>HeroProfileLoader.ReMintInventory</c> precedent). Modifier/persistent descriptor slots round-trip by the
    /// <see cref="CanonicalEffectDescriptorTable"/> index. Godot-free, Tier-1.</para>
    ///
    /// <para><b>Body format.</b> <see cref="WriteBody"/>/<see cref="ReadBody"/> emit length-framed, type-tagged sections
    /// terminated by a zero-length frame; the reader throws <see cref="InvalidDataException"/> on an unknown section tag
    /// or truncation (never a silent partial load). The framing lives here; <see cref="SaveGameFile"/> owns the header
    /// + fail-closed version/hash gating around it.</para>
    /// </summary>
    public sealed class SaveGameState
    {
        // ── Scalars ──
        public uint  Tick;
        public ulong RngState;

        // ── EntityWorld ── (all captured as int/enum-as-int/Fixed.Raw; strings for the def ref)
        public int   EntHwm;
        public int   EntAliveCount;
        public int[] EntFreeList = Array.Empty<int>();
        public int[][] Ent = Array.Empty<int[]>();       // indexed by (int)EA, each length EntHwm (or EntHwm*stride for flat)
        public string[] EntDefId = Array.Empty<string>();  // per entity, "" = def-less

        // ── BuildingStore ──
        public int   BCount;
        public int[] BFreeList = Array.Empty<int>();
        public int[][] Bld = Array.Empty<int[]>();
        public string[]   BDefinitionId = Array.Empty<string>();
        public string[][] BShopStock    = Array.Empty<string[]>();

        // ── ResourceStore ── (per FACTION_ARRAY_SIZE)
        public int[] ResOre = Array.Empty<int>();
        public int[] ResCrystal = Array.Empty<int>();
        public int[] ResSupplyUsed = Array.Empty<int>();
        public int[] ResSupplyCap = Array.Empty<int>();
        public int[] ResBaseX = Array.Empty<int>();
        public int[] ResBaseY = Array.Empty<int>();
        public int[] ResBaseZ = Array.Empty<int>();

        // ── ResourceNodeStore ──
        public int   NodeCount;
        public int[][] Node = Array.Empty<int[]>();
        public string[] NodeRequiresStructureId = Array.Empty<string>();

        // ── HeroStore ──
        public int   HeroCount;
        public int[] HeroFreeList = Array.Empty<int>();
        public int[][] Hero = Array.Empty<int[]>();
        public ulong[] HeroId = Array.Empty<ulong>();
        public string[] HeroDefId = Array.Empty<string>();

        // ── ItemStore ──
        public int   ItemCount;
        public int[] ItemFreeList = Array.Empty<int>();
        public int[][] Item = Array.Empty<int[]>();

        // ── ProjectileStore ──
        public int   ProjHwm;
        public int[] ProjFreeList = Array.Empty<int>();
        public int[][] Proj = Array.Empty<int[]>();

        // ── ResearchStore ── (per faction, jagged inner per-research)
        public int[] ReschInProgress = Array.Empty<int>();
        public int[] ReschRemaining = Array.Empty<int>();
        public int[] ReschStartedX = Array.Empty<int>();
        public int[] ReschStartedY = Array.Empty<int>();
        public int[] ReschStartedZ = Array.Empty<int>();
        public int[][] ReschCompleted = Array.Empty<int[]>();
        public int[][] ReschCumHp = Array.Empty<int[]>();
        public int[][] ReschCumAtk = Array.Empty<int[]>();
        public int[][] ReschCumMove = Array.Empty<int[]>();
        public int[][] ReschCumArmor = Array.Empty<int[]>();

        // ── WinStateStore ──
        public int   WinMatchTicks;
        public int[] WinKoth = Array.Empty<int>();
        public int[] WinSurvival = Array.Empty<int>();
        public int[] WinVerdict = Array.Empty<int>();

        // ── AllianceStore ──
        public int[] AllianceTeam = Array.Empty<int>();

        // ── TriggerEnabledStore ──
        public int[] TriggerEnabled = Array.Empty<int>(); // 0/1 per exec index

        // ── ModifierStore ── (per entity: count then per slot the descriptor kind/index + folded ints + caster)
        public ModifierEntry[] Modifiers = Array.Empty<ModifierEntry>();

        // ── ScenarioDirector runtime ──
        public int[] DirFired = Array.Empty<int>();     // 0/1 per exec
        public int[] DirCooldown = Array.Empty<int>();
        public int   DirFirstTick;

        // ── DslVarTable ──
        public DslVarTable.Snapshot DslVars = new();

        // ── DslLoopState ──
        public int   LoopFuel;
        public int[] LoopRowActive = Array.Empty<int>();
        public int[] LoopRowCursor = Array.Empty<int>();
        public int[] LoopRowLen = Array.Empty<int>();
        public int[][] LoopRowIds = Array.Empty<int[]>();

        // ── DslEventQueue ──
        public int[] DslEvIndex = Array.Empty<int>();
        public int[] DslEvRaiser = Array.Empty<int>();
        public int[][] DslEvParams = Array.Empty<int[]>();

        // ── AiOpponentSystem ──
        public int[] AiProductionIds = Array.Empty<int>();
        public int   AiCmdCenterExpId;
        public int   AiAttackCooldownRaw;

        // ── MatchStats (observational scoreboard; unfolded, but must round-trip for the 11.2 score screen) ──
        public int[][] Stats = Array.Empty<int[]>();

        /// <summary>One captured active <see cref="ModifierStore"/> slot (a Modifier or a PersistentEffect descriptor
        /// serialized by its <see cref="CanonicalEffectDescriptorTable"/> index).</summary>
        public struct ModifierEntry
        {
            public int HostId;
            public byte Kind;        // 0 = Modifier, 1 = PersistentEffect
            public int DescIndex;    // canonical descriptor-table index
            public int ModifierId;
            public int RemainingTicks;
            public int TicksUntilPeriod;
            public int PeriodsRemaining;
            public int StackCount;
            public int CasterId;
            public int CasterFaction;
        }

        // ── EntityWorld per-entity array positions (named so capture/restore reference the SAME index). ──
        private enum EA
        {
            Flags, PosX, PosY, PosZ, VelX, VelY, VelZ, BaseMoveSpeed, EffMoveSpeed, Health, BaseMaxHealth, EffMaxHealth,
            FactionOf, MoveTargetX, MoveTargetY, MoveTargetZ, AttackTarget, AttackCooldown, AttackRange, BaseAtkDmg,
            EffAtkDmg, BaseArmor, EffArmor, AttackSpeed, Energy, MaxEnergy, StatusFlags, DamageType, ArmorType,
            VisionRange, Elevation, SplashRadius, Delivery, ProjectileSpeed, XpBounty, KillerOf, KillerFactionOf,
            CollisionRadius, SepPriority, Category, AttackDomain, Tags, SupplyCost, MeshType, CommandState,
            CommandGoalX, CommandGoalY, CommandGoalZ, CommandTarget, PatrolCount, PatrolIndex, PatrolDir,
            OrderQueueCount, ActiveOrderCmd, AbilityCount, PendingCastSlot, PendingCastTarget, AuraIdx, OnHitIdx,
            SelfPassiveIdx, HeroIndex, GatherState, GatherTarget, CarryAmount, CarryResType, CarryCapacity, BuildTarget,
            // flat (stride) arrays — length EntHwm * stride
            PatrolWpX, PatrolWpY, PatrolWpZ, OrderQCmd, OrderQTargetX, OrderQTargetZ, AbilityId, AbilityCd,
            COUNT
        }

        // ── BuildingStore array positions. ──
        private enum BA
        {
            Alive, PosX, PosY, PosZ, FactionOf, Type, Health, MaxHealth, SupplyBonus, ConstructionTimer,
            ConstructionDuration, ProductionTimer, ProductionQueue, RallyX, RallyY, RallyZ, HasRally, TrainedCount,
            RevivesHeroes, SellsItems, ShopRadius, Generation, COUNT
        }

        // ── ResourceNodeStore array positions. ──
        private enum NA
        {
            Active, PosX, PosY, PosZ, SupplyRemaining, SupplyTotal, GatherRate, MaxGatherers, AssignedGatherers,
            CollectionModel, ResourceType, RequiresStructureRadius, OwnerFaction, IncomePeriodTicks, IncomeTicksElapsed,
            COUNT
        }

        // ── HeroStore array positions (excl. Id/SourceDef handled separately). ──
        private enum HA
        {
            Alive, EntityId, Level, Xp, GrowthStacksApplied, MaxLevelOf, BaseXpOf, XpGrowthOf, XpShareRadiusOf,
            HealthPerLevelOf, DamagePerLevelOf, ArmorPerLevelOf, Alive3_14, AwaitingRevival, RevivalTimer, RevivalLink,
            OwnerFaction, Generation, Inventory, COUNT
        }

        // ── ItemStore array positions. ──
        private enum IA { Alive, DefId, Charges, PosX, PosZ, Held, CarrierHeroSlot, Generation, COUNT }

        // ── ProjectileStore array positions (Feedback is presentation-only → not captured). ──
        private enum PA
        {
            Alive, PosX, PosY, PosZ, TargetId, LastKnownX, LastKnownY, LastKnownZ, Damage, DmgType, TargetArmor, Owner,
            SplashRadius, Speed, TargetIsBuilding, SourceId, COUNT
        }

        // ═══════════════════════════════════ CAPTURE ═══════════════════════════════════

        /// <summary>Snapshot every mutable store off <paramref name="host"/> into a new <see cref="SaveGameState"/>.
        /// Modifier/persistent descriptor slots serialize by <paramref name="table"/> index; a descriptor unreachable
        /// by the canonical walk throws (the <c>modifier descriptor round-trip needs a content-model change</c> Block-If).</summary>
        public static SaveGameState CaptureFrom(SimulationHost host, CanonicalEffectDescriptorTable table)
        {
            var s = new SaveGameState { Tick = host.CurrentTick, RngState = host.World.Rng.State };
            s.CaptureEntities(host.World);
            s.CaptureBuildings(host.Buildings);
            s.CaptureResources(host.Resources);
            s.CaptureNodes(host.Nodes);
            s.CaptureHeroes(host.Heroes);
            s.CaptureItems(host.Items);
            s.CaptureProjectiles(host.Projectiles);
            s.CaptureResearch(host.Research);
            s.CaptureWinState(host.WinState);
            s.CaptureAlliances(host.Alliances);
            s.CaptureTriggerEnabled(host.TriggerEnabled);
            s.CaptureModifiers(host.World, host.Modifiers, table);
            s.CaptureDirector(host.ScenarioDirector);
            s.DslVars = host.Vars.Capture();
            s.CaptureLoopState(host.LoopState);
            s.CaptureDslEvents(host.DslEvents);
            s.CaptureAi(host.Ai);
            s.Stats = host.MatchStats.CaptureCounters();
            return s;
        }

        private void CaptureEntities(EntityWorld w)
        {
            int n = w.HighWaterMark;
            EntHwm = n;
            EntAliveCount = w.AliveCount;
            EntFreeList = w.CaptureFreeList();
            Ent = new int[(int)EA.COUNT][];
            int[] A(EA e, int len) { var a = new int[len]; Ent[(int)e] = a; return a; }

            var flags = A(EA.Flags, n); var px = A(EA.PosX, n); var py = A(EA.PosY, n); var pz = A(EA.PosZ, n);
            var vx = A(EA.VelX, n); var vy = A(EA.VelY, n); var vz = A(EA.VelZ, n);
            var bms = A(EA.BaseMoveSpeed, n); var ems = A(EA.EffMoveSpeed, n);
            var hp = A(EA.Health, n); var bmh = A(EA.BaseMaxHealth, n); var emh = A(EA.EffMaxHealth, n);
            var fac = A(EA.FactionOf, n); var mtx = A(EA.MoveTargetX, n); var mty = A(EA.MoveTargetY, n); var mtz = A(EA.MoveTargetZ, n);
            var at = A(EA.AttackTarget, n); var ac = A(EA.AttackCooldown, n); var ar = A(EA.AttackRange, n);
            var bad = A(EA.BaseAtkDmg, n); var ead = A(EA.EffAtkDmg, n); var bar = A(EA.BaseArmor, n); var ear = A(EA.EffArmor, n);
            var asp = A(EA.AttackSpeed, n); var en = A(EA.Energy, n); var men = A(EA.MaxEnergy, n); var sf = A(EA.StatusFlags, n);
            var dt = A(EA.DamageType, n); var art = A(EA.ArmorType, n); var vr = A(EA.VisionRange, n); var el = A(EA.Elevation, n);
            var spl = A(EA.SplashRadius, n); var del = A(EA.Delivery, n); var psp = A(EA.ProjectileSpeed, n); var xpb = A(EA.XpBounty, n);
            var ko = A(EA.KillerOf, n); var kf = A(EA.KillerFactionOf, n); var cr = A(EA.CollisionRadius, n);
            var sep = A(EA.SepPriority, n); var cat = A(EA.Category, n); var ad = A(EA.AttackDomain, n); var tg = A(EA.Tags, n);
            var sc = A(EA.SupplyCost, n); var mesh = A(EA.MeshType, n); var cs = A(EA.CommandState, n);
            var cgx = A(EA.CommandGoalX, n); var cgy = A(EA.CommandGoalY, n); var cgz = A(EA.CommandGoalZ, n); var ct = A(EA.CommandTarget, n);
            var pc = A(EA.PatrolCount, n); var pidx = A(EA.PatrolIndex, n); var pdir = A(EA.PatrolDir, n);
            var oqc = A(EA.OrderQueueCount, n); var aoc = A(EA.ActiveOrderCmd, n); var abc = A(EA.AbilityCount, n);
            var pcs = A(EA.PendingCastSlot, n); var pct = A(EA.PendingCastTarget, n);
            var aura = A(EA.AuraIdx, n); var onhit = A(EA.OnHitIdx, n); var selfp = A(EA.SelfPassiveIdx, n); var hidx = A(EA.HeroIndex, n);
            var gs = A(EA.GatherState, n); var gt = A(EA.GatherTarget, n); var ca = A(EA.CarryAmount, n);
            var crt = A(EA.CarryResType, n); var cc = A(EA.CarryCapacity, n); var bt = A(EA.BuildTarget, n);
            EntDefId = new string[n];

            for (int i = 0; i < n; i++)
            {
                flags[i] = (int)w.Flags[i]; px[i] = w.Position[i].X.Raw; py[i] = w.Position[i].Y.Raw; pz[i] = w.Position[i].Z.Raw;
                vx[i] = w.Velocity[i].X.Raw; vy[i] = w.Velocity[i].Y.Raw; vz[i] = w.Velocity[i].Z.Raw;
                bms[i] = w.BaseMoveSpeed[i].Raw; ems[i] = w.EffectiveMoveSpeed[i].Raw;
                hp[i] = w.Health[i].Raw; bmh[i] = w.BaseMaxHealth[i].Raw; emh[i] = w.EffectiveMaxHealth[i].Raw;
                fac[i] = (int)w.FactionOf[i]; mtx[i] = w.MoveTarget[i].X.Raw; mty[i] = w.MoveTarget[i].Y.Raw; mtz[i] = w.MoveTarget[i].Z.Raw;
                at[i] = w.AttackTarget[i]; ac[i] = w.AttackCooldown[i].Raw; ar[i] = w.AttackRange[i].Raw;
                bad[i] = w.BaseAttackDamage[i].Raw; ead[i] = w.EffectiveAttackDamage[i].Raw; bar[i] = w.BaseArmor[i].Raw; ear[i] = w.EffectiveArmor[i].Raw;
                asp[i] = w.AttackSpeed[i].Raw; en[i] = w.Energy[i].Raw; men[i] = w.MaxEnergy[i].Raw; sf[i] = (int)w.StatusFlagsOf[i];
                dt[i] = (int)w.DamageTypeOf[i]; art[i] = (int)w.ArmorTypeOf[i]; vr[i] = w.VisionRange[i].Raw; el[i] = w.Elevation[i].Raw;
                spl[i] = w.SplashRadius[i].Raw; del[i] = (int)w.Delivery[i]; psp[i] = w.ProjectileSpeed[i].Raw; xpb[i] = w.XpBounty[i].Raw;
                ko[i] = w.KillerOf[i]; kf[i] = w.KillerFactionOf[i]; cr[i] = w.CollisionRadius[i].Raw;
                sep[i] = (int)w.SeparationPriorityOf[i]; cat[i] = (int)w.CategoryOf[i]; ad[i] = (int)w.AttackDomainOf[i]; tg[i] = (int)w.TagsOf[i];
                sc[i] = w.SupplyCost[i]; mesh[i] = w.MeshType[i]; cs[i] = (int)w.CommandState[i];
                cgx[i] = w.CommandGoal[i].X.Raw; cgy[i] = w.CommandGoal[i].Y.Raw; cgz[i] = w.CommandGoal[i].Z.Raw; ct[i] = w.CommandTarget[i];
                pc[i] = w.PatrolCount[i]; pidx[i] = w.PatrolIndex[i]; pdir[i] = w.PatrolDir[i];
                oqc[i] = w.OrderQueueCount[i]; aoc[i] = w.ActiveOrderCmd[i]; abc[i] = w.AbilityCount[i];
                pcs[i] = w.PendingCastSlot[i]; pct[i] = w.PendingCastTarget[i];
                aura[i] = w.AuraAbilityIndex[i]; onhit[i] = w.OnHitAbilityIndex[i]; selfp[i] = w.SelfPassiveAbilityIndex[i]; hidx[i] = w.HeroIndex[i];
                gs[i] = (int)w.GatherState[i]; gt[i] = w.GatherTarget[i]; ca[i] = w.CarryAmount[i].Raw;
                crt[i] = (int)w.CarryResourceType[i]; cc[i] = w.CarryCapacity[i].Raw; bt[i] = w.BuildTarget[i];
                EntDefId[i] = w.SourceDefinition[i]?.Id ?? "";
            }

            // Flat (stride) arrays.
            int wp = n * EntityWorld.MAX_PATROL_WAYPOINTS;
            var wpx = A(EA.PatrolWpX, wp); var wpy = A(EA.PatrolWpY, wp); var wpz = A(EA.PatrolWpZ, wp);
            for (int i = 0; i < wp; i++) { wpx[i] = w.PatrolWaypoints[i].X.Raw; wpy[i] = w.PatrolWaypoints[i].Y.Raw; wpz[i] = w.PatrolWaypoints[i].Z.Raw; }
            int oq = n * EntityWorld.MAX_ORDER_QUEUE;
            var oqcmd = A(EA.OrderQCmd, oq); var oqtx = A(EA.OrderQTargetX, oq); var oqtz = A(EA.OrderQTargetZ, oq);
            for (int i = 0; i < oq; i++) { oqcmd[i] = w.OrderQueueCmd[i]; oqtx[i] = w.OrderQueueTargetX[i]; oqtz[i] = w.OrderQueueTargetZ[i]; }
            int ab = n * EntityWorld.MAX_ABILITIES_PER_UNIT;
            var abid = A(EA.AbilityId, ab); var abcd = A(EA.AbilityCd, ab);
            for (int i = 0; i < ab; i++) { abid[i] = w.AbilityId[i]; abcd[i] = w.AbilityCooldownTicks[i]; }
        }

        private void CaptureBuildings(BuildingStore b)
        {
            int n = b.Count;
            BCount = n;
            BFreeList = b.CaptureFreeList();
            Bld = new int[(int)BA.COUNT][];
            int[] A(BA e) { var a = new int[n]; Bld[(int)e] = a; return a; }
            var al = A(BA.Alive); var px = A(BA.PosX); var py = A(BA.PosY); var pz = A(BA.PosZ); var fac = A(BA.FactionOf);
            var ty = A(BA.Type); var hp = A(BA.Health); var mh = A(BA.MaxHealth); var sb = A(BA.SupplyBonus);
            var cti = A(BA.ConstructionTimer); var cdu = A(BA.ConstructionDuration); var pti = A(BA.ProductionTimer);
            var pq = A(BA.ProductionQueue); var rx = A(BA.RallyX); var ry = A(BA.RallyY); var rz = A(BA.RallyZ);
            var hr = A(BA.HasRally); var tc = A(BA.TrainedCount); var rh = A(BA.RevivesHeroes); var si = A(BA.SellsItems);
            var srd = A(BA.ShopRadius); var gen = A(BA.Generation);
            BDefinitionId = new string[n]; BShopStock = new string[n][];
            for (int i = 0; i < n; i++)
            {
                al[i] = b.Alive[i] ? 1 : 0; px[i] = b.Position[i].X.Raw; py[i] = b.Position[i].Y.Raw; pz[i] = b.Position[i].Z.Raw;
                fac[i] = (int)b.FactionOf[i]; ty[i] = (int)b.Type[i]; hp[i] = b.Health[i].Raw; mh[i] = b.MaxHealth[i].Raw;
                sb[i] = b.SupplyBonus[i]; cti[i] = b.ConstructionTimer[i].Raw; cdu[i] = b.ConstructionDuration[i].Raw;
                pti[i] = b.ProductionTimer[i].Raw; pq[i] = b.ProductionQueue[i];
                rx[i] = b.RallyPoint[i].X.Raw; ry[i] = b.RallyPoint[i].Y.Raw; rz[i] = b.RallyPoint[i].Z.Raw;
                hr[i] = b.HasRallyPoint[i] ? 1 : 0; tc[i] = b.TrainedCount[i]; rh[i] = b.RevivesHeroes[i] ? 1 : 0;
                si[i] = b.SellsItems[i] ? 1 : 0; srd[i] = b.ShopRadius[i].Raw; gen[i] = b.Generation[i];
                BDefinitionId[i] = b.DefinitionId[i] ?? "";
                // Review fix: CLONE rather than alias the live string[]. Harmless on the disk path (serialization
                // copies), but the in-memory CaptureFrom→RestoreInto path shared one mutable array with the sim in
                // both directions — every other reference-typed lane in this file is cloned or round-tripped by id.
                string[]? stock = b.ShopStock[i];
                BShopStock[i] = stock == null || stock.Length == 0 ? Array.Empty<string>() : (string[])stock.Clone();
            }
        }

        private void CaptureResources(ResourceStore r)
        {
            int n = r.Ore.Length;
            ResOre = new int[n]; ResCrystal = new int[n]; ResSupplyUsed = new int[n]; ResSupplyCap = new int[n];
            ResBaseX = new int[n]; ResBaseY = new int[n]; ResBaseZ = new int[n];
            for (int i = 0; i < n; i++)
            {
                ResOre[i] = r.Ore[i].Raw; ResCrystal[i] = r.Crystal[i].Raw; ResSupplyUsed[i] = r.SupplyUsed[i]; ResSupplyCap[i] = r.SupplyCap[i];
                ResBaseX[i] = r.FactionBase[i].X.Raw; ResBaseY[i] = r.FactionBase[i].Y.Raw; ResBaseZ[i] = r.FactionBase[i].Z.Raw;
            }
        }

        private void CaptureNodes(ResourceNodeStore nodes)
        {
            int n = nodes.Count;
            NodeCount = n;
            Node = new int[(int)NA.COUNT][];
            int[] A(NA e) { var a = new int[n]; Node[(int)e] = a; return a; }
            var ac = A(NA.Active); var px = A(NA.PosX); var py = A(NA.PosY); var pz = A(NA.PosZ);
            var sr = A(NA.SupplyRemaining); var st = A(NA.SupplyTotal); var gr = A(NA.GatherRate); var mg = A(NA.MaxGatherers);
            var ag = A(NA.AssignedGatherers); var cm = A(NA.CollectionModel); var rt = A(NA.ResourceType);
            var rsr = A(NA.RequiresStructureRadius); var of = A(NA.OwnerFaction); var ipt = A(NA.IncomePeriodTicks); var ite = A(NA.IncomeTicksElapsed);
            NodeRequiresStructureId = new string[n];
            for (int i = 0; i < n; i++)
            {
                ac[i] = nodes.Active[i] ? 1 : 0; px[i] = nodes.Position[i].X.Raw; py[i] = nodes.Position[i].Y.Raw; pz[i] = nodes.Position[i].Z.Raw;
                sr[i] = nodes.SupplyRemaining[i].Raw; st[i] = nodes.SupplyTotal[i].Raw; gr[i] = nodes.GatherRate[i].Raw; mg[i] = nodes.MaxGatherers[i];
                ag[i] = nodes.AssignedGatherers[i]; cm[i] = (int)nodes.CollectionModel[i]; rt[i] = (int)nodes.ResourceType[i];
                rsr[i] = nodes.RequiresStructureRadius[i].Raw; of[i] = (int)nodes.OwnerFaction[i]; ipt[i] = nodes.IncomePeriodTicks[i]; ite[i] = nodes.IncomeTicksElapsed[i];
                NodeRequiresStructureId[i] = nodes.RequiresStructureId[i] ?? "";
            }
        }

        private void CaptureHeroes(HeroStore h)
        {
            int n = h.Count;
            HeroCount = n;
            HeroFreeList = h.CaptureFreeList();
            Hero = new int[(int)HA.COUNT][];
            int[] A(HA e, int len) { var a = new int[len]; Hero[(int)e] = a; return a; }
            var al = A(HA.Alive, n); var eid = A(HA.EntityId, n); var lv = A(HA.Level, n); var xp = A(HA.Xp, n);
            var gsa = A(HA.GrowthStacksApplied, n); var ml = A(HA.MaxLevelOf, n); var bx = A(HA.BaseXpOf, n); var xg = A(HA.XpGrowthOf, n);
            var xsr = A(HA.XpShareRadiusOf, n); var hpl = A(HA.HealthPerLevelOf, n); var dpl = A(HA.DamagePerLevelOf, n); var apl = A(HA.ArmorPerLevelOf, n);
            var a314 = A(HA.Alive3_14, n); var awr = A(HA.AwaitingRevival, n); var rti = A(HA.RevivalTimer, n); var rlk = A(HA.RevivalLink, n);
            var of = A(HA.OwnerFaction, n); var gen = A(HA.Generation, n);
            var inv = A(HA.Inventory, n * HeroStore.INVENTORY_SLOTS);
            HeroId = new ulong[n]; HeroDefId = new string[n];
            for (int i = 0; i < n; i++)
            {
                al[i] = h.Alive[i] ? 1 : 0; eid[i] = h.EntityId[i]; lv[i] = h.Level[i]; xp[i] = h.Xp[i].Raw;
                gsa[i] = h.GrowthStacksApplied[i]; ml[i] = h.MaxLevelOf[i]; bx[i] = h.BaseXpOf[i].Raw; xg[i] = h.XpGrowthOf[i].Raw;
                xsr[i] = h.XpShareRadiusOf[i].Raw; hpl[i] = h.HealthPerLevelOf[i].Raw; dpl[i] = h.DamagePerLevelOf[i].Raw; apl[i] = h.ArmorPerLevelOf[i].Raw;
                a314[i] = h.Alive3_14[i] ? 1 : 0; awr[i] = h.AwaitingRevival[i] ? 1 : 0; rti[i] = h.RevivalTimer[i].Raw; rlk[i] = h.RevivalLink[i];
                of[i] = (int)h.OwnerFaction[i]; gen[i] = h.Generation[i];
                HeroId[i] = h.Id[i].Value; HeroDefId[i] = h.SourceDef[i]?.Id ?? "";
            }
            for (int i = 0; i < n * HeroStore.INVENTORY_SLOTS; i++) inv[i] = h.Inventory[i];
        }

        private void CaptureItems(ItemStore it)
        {
            int n = it.Count;
            ItemCount = n;
            ItemFreeList = it.CaptureFreeList();
            Item = new int[(int)IA.COUNT][];
            int[] A(IA e) { var a = new int[n]; Item[(int)e] = a; return a; }
            var al = A(IA.Alive); var df = A(IA.DefId); var ch = A(IA.Charges); var px = A(IA.PosX); var pz = A(IA.PosZ);
            var hd = A(IA.Held); var cs = A(IA.CarrierHeroSlot); var gen = A(IA.Generation);
            for (int i = 0; i < n; i++)
            {
                al[i] = it.Alive[i] ? 1 : 0; df[i] = it.DefId[i]; ch[i] = it.Charges[i]; px[i] = it.PosX[i].Raw; pz[i] = it.PosZ[i].Raw;
                hd[i] = it.Held[i] ? 1 : 0; cs[i] = it.CarrierHeroSlot[i]; gen[i] = it.Generation[i];
            }
        }

        private void CaptureProjectiles(ProjectileStore p)
        {
            int n = p.HighWaterMark;
            ProjHwm = n;
            ProjFreeList = p.CaptureFreeList();
            Proj = new int[(int)PA.COUNT][];
            int[] A(PA e) { var a = new int[n]; Proj[(int)e] = a; return a; }
            var al = A(PA.Alive); var px = A(PA.PosX); var py = A(PA.PosY); var pz = A(PA.PosZ); var ti = A(PA.TargetId);
            var lx = A(PA.LastKnownX); var ly = A(PA.LastKnownY); var lz = A(PA.LastKnownZ); var dm = A(PA.Damage);
            var dty = A(PA.DmgType); var ta = A(PA.TargetArmor); var ow = A(PA.Owner); var sr = A(PA.SplashRadius);
            var sp = A(PA.Speed); var tib = A(PA.TargetIsBuilding); var sid = A(PA.SourceId);
            for (int i = 0; i < n; i++)
            {
                al[i] = p.Alive[i] ? 1 : 0; px[i] = p.Position[i].X.Raw; py[i] = p.Position[i].Y.Raw; pz[i] = p.Position[i].Z.Raw;
                ti[i] = p.TargetId[i]; lx[i] = p.LastKnownPos[i].X.Raw; ly[i] = p.LastKnownPos[i].Y.Raw; lz[i] = p.LastKnownPos[i].Z.Raw;
                dm[i] = p.Damage[i].Raw; dty[i] = (int)p.DmgType[i]; ta[i] = (int)p.TargetArmor[i]; ow[i] = (int)p.Owner[i];
                sr[i] = p.SplashRadius[i].Raw; sp[i] = p.Speed[i].Raw; tib[i] = p.TargetIsBuilding[i] ? 1 : 0; sid[i] = p.SourceId[i];
            }
        }

        private void CaptureResearch(ResearchStore r)
        {
            int f = r.InProgressIndex.Length;
            ReschInProgress = new int[f]; ReschRemaining = new int[f];
            ReschStartedX = new int[f]; ReschStartedY = new int[f]; ReschStartedZ = new int[f];
            ReschCompleted = new int[f][]; ReschCumHp = new int[f][]; ReschCumAtk = new int[f][]; ReschCumMove = new int[f][]; ReschCumArmor = new int[f][];
            for (int i = 0; i < f; i++)
            {
                ReschInProgress[i] = r.InProgressIndex[i]; ReschRemaining[i] = r.RemainingTicks[i];
                ReschStartedX[i] = r.StartedAtPosition[i].X.Raw; ReschStartedY[i] = r.StartedAtPosition[i].Y.Raw; ReschStartedZ[i] = r.StartedAtPosition[i].Z.Raw;
                int m = r.CompletedLevels[i].Length;
                var cl = new int[m]; var ch = new int[m]; var ca = new int[m]; var cmv = new int[m]; var car = new int[m];
                for (int k = 0; k < m; k++)
                {
                    cl[k] = r.CompletedLevels[i][k]; ch[k] = r.CumulativeMaxHealthDelta[i][k].Raw; ca[k] = r.CumulativeAttackDamageDelta[i][k].Raw;
                    cmv[k] = r.CumulativeMoveSpeedDelta[i][k].Raw; car[k] = r.CumulativeArmorDelta[i][k].Raw;
                }
                ReschCompleted[i] = cl; ReschCumHp[i] = ch; ReschCumAtk[i] = ca; ReschCumMove[i] = cmv; ReschCumArmor[i] = car;
            }
        }

        private void CaptureWinState(WinStateStore w)
        {
            int f = w.KothHoldTicks.Length;
            WinMatchTicks = w.MatchTicks;
            WinKoth = new int[f]; WinSurvival = new int[f]; WinVerdict = new int[f];
            for (int i = 0; i < f; i++) { WinKoth[i] = w.KothHoldTicks[i]; WinSurvival[i] = w.SurvivalRemaining[i]; WinVerdict[i] = w.Verdict[i]; }
        }

        private void CaptureAlliances(AllianceStore a)
        {
            int f = a.TeamId.Length;
            AllianceTeam = new int[f];
            for (int i = 0; i < f; i++) AllianceTeam[i] = a.TeamId[i];
        }

        private void CaptureTriggerEnabled(TriggerEnabledStore t)
        {
            TriggerEnabled = new int[t.Count];
            for (int i = 0; i < t.Count; i++) TriggerEnabled[i] = t.IsEnabled(i) ? 1 : 0;
        }

        private void CaptureModifiers(EntityWorld w, ModifierStore m, CanonicalEffectDescriptorTable table)
        {
            var list = new List<ModifierEntry>();
            int cap = w.HighWaterMark;
            for (int id = 0; id < cap; id++)
            {
                int cnt = m.CountAt(id);
                for (int slot = 0; slot < cnt; slot++)
                {
                    Modifier? mod = m.ModifierRefAt(id, slot);
                    PersistentEffect? pe = m.PersistentRefAt(id, slot);
                    var e = new ModifierEntry
                    {
                        HostId = id,
                        ModifierId = m.ModifierIdAt(id, slot),
                        RemainingTicks = m.RemainingTicksAt(id, slot),
                        TicksUntilPeriod = m.TicksUntilPeriodAt(id, slot),
                        PeriodsRemaining = m.PeriodsRemainingAt(id, slot),
                        StackCount = m.StackCountAt(id, slot),
                        CasterId = m.CasterIdAt(id, slot),
                        CasterFaction = (int)m.CasterFactionAt(id, slot),
                    };
                    if (mod != null)
                    {
                        e.Kind = 0;
                        e.DescIndex = table.IndexOfModifier(mod);
                    }
                    else if (pe != null)
                    {
                        e.Kind = 1;
                        e.DescIndex = table.IndexOfPersistent(pe);
                    }
                    else
                    {
                        // A slot inside [0, count) that holds NEITHER descriptor means the store's dense-window invariant
                        // is broken (CompactSlot keeps [0,count) non-null). Skipping it via `continue` would rebuild a
                        // count one short → a non-byte-identical resume. Fail-closed: a hole is corrupt sim state.
                        throw new InvalidOperationException(
                            $"SP save: ModifierStore slot {slot} on entity {id} is within count but holds no descriptor (corrupt store state).");
                    }
                    if (e.DescIndex < 0)
                        throw new InvalidOperationException(
                            "SP save/load: an active modifier/persistent descriptor is unreachable by the canonical " +
                            "effect-descriptor table (modifier descriptor round-trip needs a content-model change).");
                    list.Add(e);
                }
            }
            Modifiers = list.ToArray();
        }

        private void CaptureDirector(ScenarioDirector d)
        {
            bool[] fired = d.CaptureTriggerFired();
            int[] cd = d.CaptureTriggerCooldown();
            DirFired = new int[fired.Length];
            for (int i = 0; i < fired.Length; i++) DirFired[i] = fired[i] ? 1 : 0;
            DirCooldown = cd;
            DirFirstTick = d.CaptureFirstTick() ? 1 : 0;
        }

        private void CaptureLoopState(DslLoopState l)
        {
            int n = l.RowCount;
            LoopFuel = l.FuelConsumed;
            LoopRowActive = new int[n]; LoopRowCursor = new int[n]; LoopRowLen = new int[n]; LoopRowIds = new int[n][];
            for (int r = 0; r < n; r++)
            {
                LoopRowActive[r] = l.RowActive(r) ? 1 : 0; LoopRowCursor[r] = l.RowCursor(r);
                int len = l.RowLength(r); LoopRowLen[r] = len;
                var ids = new int[len];
                for (int k = 0; k < len; k++) ids[k] = l.RowId(r, k);
                LoopRowIds[r] = ids;
            }
        }

        private void CaptureDslEvents(DslEventQueue q)
        {
            int n = q.Count;
            DslEvIndex = new int[n]; DslEvRaiser = new int[n]; DslEvParams = new int[n][];
            for (int i = 0; i < n; i++)
            {
                DslEvIndex[i] = q.EventIndexAt(i); DslEvRaiser[i] = q.RaiserAt(i);
                var pr = new int[EventBounds.MaxEventParams];
                for (int p = 0; p < EventBounds.MaxEventParams; p++) pr[p] = q.ParamAt(i, p);
                DslEvParams[i] = pr;
            }
        }

        private void CaptureAi(AiOpponentSystem ai)
        {
            AiProductionIds = ai.CaptureProductionBuildingIds();
            AiCmdCenterExpId = ai.CaptureCmdCenterExpId();
            AiAttackCooldownRaw = ai.CaptureAttackCooldownRaw();
        }

        // ═══════════════════════════════════ RESTORE ═══════════════════════════════════

        /// <summary>Overlay this snapshot onto a scenario-re-applied <paramref name="host"/>. <paramref name="slotDefs"/>
        /// (the per-slot faction definitions) re-resolves reference-typed SoA by def-id; <paramref name="table"/>
        /// re-resolves modifier/persistent descriptors by index. Full replace, not a merge.</summary>
        public void RestoreInto(SimulationHost host, CanonicalEffectDescriptorTable table, FactionDefinition?[] slotDefs)
        {
            RestoreEntities(host.World, slotDefs);
            RestoreBuildings(host.Buildings);
            RestoreResources(host.Resources);
            RestoreNodes(host.Nodes);
            RestoreHeroes(host.Heroes, slotDefs);
            RestoreItems(host.Items);
            RestoreProjectiles(host.Projectiles);
            RestoreResearch(host.Research);
            RestoreWinState(host.WinState);
            RestoreAlliances(host.Alliances);
            RestoreTriggerEnabled(host.TriggerEnabled);
            RestoreModifiers(host.Modifiers, table);
            RestoreDirector(host.ScenarioDirector, host.World);
            host.Vars.Restore(DslVars);
            RestoreLoopState(host.LoopState);
            RestoreDslEvents(host.DslEvents);
            RestoreAi(host.Ai);
            host.MatchStats.RestoreCounters(Stats);
            host.World.Rng.Seed(RngState);
            host.RestoreTick(Tick);
        }

        private static UnitDefinition? ResolveDef(FactionDefinition?[]? slotDefs, int factionIdx, string defId)
        {
            if (string.IsNullOrEmpty(defId) || slotDefs == null) return null;
            if (factionIdx < 0 || factionIdx >= slotDefs.Length) return null;
            return slotDefs[factionIdx]?.GetUnit(defId);
        }

        private void RestoreEntities(EntityWorld w, FactionDefinition?[]? slotDefs)
        {
            int n = EntHwm;
            int[] G(EA e) => Ent[(int)e];
            var flags = G(EA.Flags); var px = G(EA.PosX); var py = G(EA.PosY); var pz = G(EA.PosZ);
            var vx = G(EA.VelX); var vy = G(EA.VelY); var vz = G(EA.VelZ);
            var bms = G(EA.BaseMoveSpeed); var ems = G(EA.EffMoveSpeed); var hp = G(EA.Health); var bmh = G(EA.BaseMaxHealth); var emh = G(EA.EffMaxHealth);
            var fac = G(EA.FactionOf); var mtx = G(EA.MoveTargetX); var mty = G(EA.MoveTargetY); var mtz = G(EA.MoveTargetZ);
            var at = G(EA.AttackTarget); var ac = G(EA.AttackCooldown); var ar = G(EA.AttackRange);
            var bad = G(EA.BaseAtkDmg); var ead = G(EA.EffAtkDmg); var bar = G(EA.BaseArmor); var ear = G(EA.EffArmor);
            var asp = G(EA.AttackSpeed); var en = G(EA.Energy); var men = G(EA.MaxEnergy); var sf = G(EA.StatusFlags);
            var dt = G(EA.DamageType); var art = G(EA.ArmorType); var vr = G(EA.VisionRange); var el = G(EA.Elevation);
            var spl = G(EA.SplashRadius); var del = G(EA.Delivery); var psp = G(EA.ProjectileSpeed); var xpb = G(EA.XpBounty);
            var ko = G(EA.KillerOf); var kf = G(EA.KillerFactionOf); var cr = G(EA.CollisionRadius);
            var sep = G(EA.SepPriority); var cat = G(EA.Category); var ad = G(EA.AttackDomain); var tg = G(EA.Tags);
            var sc = G(EA.SupplyCost); var mesh = G(EA.MeshType); var cs = G(EA.CommandState);
            var cgx = G(EA.CommandGoalX); var cgy = G(EA.CommandGoalY); var cgz = G(EA.CommandGoalZ); var ct = G(EA.CommandTarget);
            var pc = G(EA.PatrolCount); var pidx = G(EA.PatrolIndex); var pdir = G(EA.PatrolDir);
            var oqc = G(EA.OrderQueueCount); var aoc = G(EA.ActiveOrderCmd); var abc = G(EA.AbilityCount);
            var pcs = G(EA.PendingCastSlot); var pct = G(EA.PendingCastTarget);
            var aura = G(EA.AuraIdx); var onhit = G(EA.OnHitIdx); var selfp = G(EA.SelfPassiveIdx); var hidx = G(EA.HeroIndex);
            var gs = G(EA.GatherState); var gt = G(EA.GatherTarget); var ca = G(EA.CarryAmount); var crt = G(EA.CarryResType); var cc = G(EA.CarryCapacity); var bt = G(EA.BuildTarget);

            for (int i = 0; i < n; i++)
            {
                w.Flags[i] = (EntityFlags)flags[i];
                w.Position[i] = new FixedVec3(Fixed.FromRaw(px[i]), Fixed.FromRaw(py[i]), Fixed.FromRaw(pz[i]));
                w.PrevPosition[i] = w.Position[i]; // transient — seeded to Position (SnapshotPositions overwrites next tick)
                w.Velocity[i] = new FixedVec3(Fixed.FromRaw(vx[i]), Fixed.FromRaw(vy[i]), Fixed.FromRaw(vz[i]));
                w.BaseMoveSpeed[i] = Fixed.FromRaw(bms[i]); w.EffectiveMoveSpeed[i] = Fixed.FromRaw(ems[i]);
                w.Health[i] = Fixed.FromRaw(hp[i]); w.BaseMaxHealth[i] = Fixed.FromRaw(bmh[i]); w.EffectiveMaxHealth[i] = Fixed.FromRaw(emh[i]);
                w.FactionOf[i] = (Faction)fac[i];
                w.MoveTarget[i] = new FixedVec3(Fixed.FromRaw(mtx[i]), Fixed.FromRaw(mty[i]), Fixed.FromRaw(mtz[i]));
                w.AttackTarget[i] = at[i]; w.AttackCooldown[i] = Fixed.FromRaw(ac[i]); w.AttackRange[i] = Fixed.FromRaw(ar[i]);
                w.BaseAttackDamage[i] = Fixed.FromRaw(bad[i]); w.EffectiveAttackDamage[i] = Fixed.FromRaw(ead[i]);
                w.BaseArmor[i] = Fixed.FromRaw(bar[i]); w.EffectiveArmor[i] = Fixed.FromRaw(ear[i]);
                w.AttackSpeed[i] = Fixed.FromRaw(asp[i]); w.Energy[i] = Fixed.FromRaw(en[i]); w.MaxEnergy[i] = Fixed.FromRaw(men[i]);
                w.StatusFlagsOf[i] = (StatusFlags)sf[i]; w.DamageTypeOf[i] = (DamageType)dt[i]; w.ArmorTypeOf[i] = (ArmorType)art[i];
                w.VisionRange[i] = Fixed.FromRaw(vr[i]); w.Elevation[i] = Fixed.FromRaw(el[i]); w.SplashRadius[i] = Fixed.FromRaw(spl[i]);
                w.Delivery[i] = (AttackDelivery)del[i]; w.ProjectileSpeed[i] = Fixed.FromRaw(psp[i]); w.XpBounty[i] = Fixed.FromRaw(xpb[i]);
                w.KillerOf[i] = ko[i]; w.KillerFactionOf[i] = kf[i]; w.CollisionRadius[i] = Fixed.FromRaw(cr[i]);
                w.SeparationPriorityOf[i] = (SeparationPriority)sep[i]; w.CategoryOf[i] = (UnitCategory)cat[i];
                w.AttackDomainOf[i] = (AttackDomain)ad[i]; w.TagsOf[i] = (UnitTag)tg[i];
                w.SupplyCost[i] = (byte)sc[i]; w.MeshType[i] = (byte)mesh[i]; w.CommandState[i] = (UnitCommand)cs[i];
                w.CommandGoal[i] = new FixedVec3(Fixed.FromRaw(cgx[i]), Fixed.FromRaw(cgy[i]), Fixed.FromRaw(cgz[i])); w.CommandTarget[i] = ct[i];
                w.PatrolCount[i] = (byte)pc[i]; w.PatrolIndex[i] = (byte)pidx[i]; w.PatrolDir[i] = (sbyte)pdir[i];
                w.OrderQueueCount[i] = (byte)oqc[i]; w.ActiveOrderCmd[i] = (byte)aoc[i]; w.AbilityCount[i] = (byte)abc[i];
                w.PendingCastSlot[i] = (byte)pcs[i]; w.PendingCastTarget[i] = pct[i];
                w.AuraAbilityIndex[i] = aura[i]; w.OnHitAbilityIndex[i] = onhit[i]; w.SelfPassiveAbilityIndex[i] = selfp[i]; w.HeroIndex[i] = hidx[i];
                w.GatherState[i] = (GatherState)gs[i]; w.GatherTarget[i] = gt[i]; w.CarryAmount[i] = Fixed.FromRaw(ca[i]);
                w.CarryResourceType[i] = (ResourceKind)crt[i]; w.CarryCapacity[i] = Fixed.FromRaw(cc[i]); w.BuildTarget[i] = bt[i];

                // Reference-typed SoA: re-resolve the def by id + faction and set the two ref fields directly (NOT via
                // ApplyUnitDefinition, which would clobber the just-restored numeric SoA and re-fire the self-passive
                // install seam). FeedbackProfile is derived from the def; both are null for a def-less spawn.
                UnitDefinition? def = ResolveDef(slotDefs, fac[i], EntDefId[i]);
                w.SourceDefinition[i] = def!;
                w.FeedbackProfile[i] = def?.CombatFeedback!;
            }

            int wp = G(EA.PatrolWpX).Length; var wpx = G(EA.PatrolWpX); var wpy = G(EA.PatrolWpY); var wpz = G(EA.PatrolWpZ);
            for (int i = 0; i < wp; i++) w.PatrolWaypoints[i] = new FixedVec3(Fixed.FromRaw(wpx[i]), Fixed.FromRaw(wpy[i]), Fixed.FromRaw(wpz[i]));
            var oqcmd = G(EA.OrderQCmd); var oqtx = G(EA.OrderQTargetX); var oqtz = G(EA.OrderQTargetZ);
            for (int i = 0; i < oqcmd.Length; i++) { w.OrderQueueCmd[i] = (byte)oqcmd[i]; w.OrderQueueTargetX[i] = oqtx[i]; w.OrderQueueTargetZ[i] = oqtz[i]; }
            var abid = G(EA.AbilityId); var abcd = G(EA.AbilityCd);
            for (int i = 0; i < abid.Length; i++) { w.AbilityId[i] = abid[i]; w.AbilityCooldownTicks[i] = abcd[i]; }

            w.RestoreAllocation(EntHwm, EntAliveCount, EntFreeList, EntFreeList.Length);
        }

        private void RestoreBuildings(BuildingStore b)
        {
            int n = BCount;
            int[] G(BA e) => Bld[(int)e];
            var al = G(BA.Alive); var px = G(BA.PosX); var py = G(BA.PosY); var pz = G(BA.PosZ); var fac = G(BA.FactionOf);
            var ty = G(BA.Type); var hp = G(BA.Health); var mh = G(BA.MaxHealth); var sb = G(BA.SupplyBonus);
            var cti = G(BA.ConstructionTimer); var cdu = G(BA.ConstructionDuration); var pti = G(BA.ProductionTimer);
            var pq = G(BA.ProductionQueue); var rx = G(BA.RallyX); var ry = G(BA.RallyY); var rz = G(BA.RallyZ);
            var hr = G(BA.HasRally); var tc = G(BA.TrainedCount); var rh = G(BA.RevivesHeroes); var si = G(BA.SellsItems);
            var srd = G(BA.ShopRadius); var gen = G(BA.Generation);
            for (int i = 0; i < n; i++)
            {
                b.Alive[i] = al[i] != 0; b.Position[i] = new FixedVec3(Fixed.FromRaw(px[i]), Fixed.FromRaw(py[i]), Fixed.FromRaw(pz[i]));
                b.FactionOf[i] = (Faction)fac[i]; b.Type[i] = (BuildingType)ty[i]; b.Health[i] = Fixed.FromRaw(hp[i]); b.MaxHealth[i] = Fixed.FromRaw(mh[i]);
                b.SupplyBonus[i] = sb[i]; b.ConstructionTimer[i] = Fixed.FromRaw(cti[i]); b.ConstructionDuration[i] = Fixed.FromRaw(cdu[i]);
                b.ProductionTimer[i] = Fixed.FromRaw(pti[i]); b.ProductionQueue[i] = (byte)pq[i];
                b.RallyPoint[i] = new FixedVec3(Fixed.FromRaw(rx[i]), Fixed.FromRaw(ry[i]), Fixed.FromRaw(rz[i]));
                b.HasRallyPoint[i] = hr[i] != 0; b.TrainedCount[i] = tc[i]; b.RevivesHeroes[i] = rh[i] != 0; b.SellsItems[i] = si[i] != 0;
                b.ShopRadius[i] = Fixed.FromRaw(srd[i]); b.Generation[i] = gen[i];
                b.DefinitionId[i] = i < BDefinitionId.Length ? BDefinitionId[i] : "";
                // Clone on the way back out too, so a restored store never shares its stock array with this state
                // object (which the caller may restore again, or keep).
                string[] stock = i < BShopStock.Length ? (BShopStock[i] ?? Array.Empty<string>()) : Array.Empty<string>();
                b.ShopStock[i] = stock.Length == 0 ? Array.Empty<string>() : (string[])stock.Clone();
            }
            b.RestoreManagement(BCount, BFreeList, BFreeList.Length);
        }

        private void RestoreResources(ResourceStore r)
        {
            // Review fix: the bound came from Ore alone while every sibling lane was indexed at the same i. Safe on the
            // disk path only because Validate ran first — but RestoreInto is public and the in-memory
            // CaptureFrom→RestoreInto path (used by tests) never validates. Bound by the shortest lane involved.
            int n = Math.Min(r.Ore.Length, ResOre.Length);
            n = Math.Min(n, Math.Min(ResCrystal.Length, Math.Min(ResSupplyUsed.Length, ResSupplyCap.Length)));
            n = Math.Min(n, Math.Min(ResBaseX.Length, Math.Min(ResBaseY.Length, ResBaseZ.Length)));
            for (int i = 0; i < n; i++)
            {
                r.Ore[i] = Fixed.FromRaw(ResOre[i]); r.Crystal[i] = Fixed.FromRaw(ResCrystal[i]); r.SupplyUsed[i] = ResSupplyUsed[i]; r.SupplyCap[i] = ResSupplyCap[i];
                r.FactionBase[i] = new FixedVec3(Fixed.FromRaw(ResBaseX[i]), Fixed.FromRaw(ResBaseY[i]), Fixed.FromRaw(ResBaseZ[i]));
            }
        }

        private void RestoreNodes(ResourceNodeStore nodes)
        {
            int n = NodeCount;
            int[] G(NA e) => Node[(int)e];
            var ac = G(NA.Active); var px = G(NA.PosX); var py = G(NA.PosY); var pz = G(NA.PosZ);
            var sr = G(NA.SupplyRemaining); var st = G(NA.SupplyTotal); var gr = G(NA.GatherRate); var mg = G(NA.MaxGatherers);
            var ag = G(NA.AssignedGatherers); var cm = G(NA.CollectionModel); var rt = G(NA.ResourceType);
            var rsr = G(NA.RequiresStructureRadius); var of = G(NA.OwnerFaction); var ipt = G(NA.IncomePeriodTicks); var ite = G(NA.IncomeTicksElapsed);
            for (int i = 0; i < n; i++)
            {
                nodes.Active[i] = ac[i] != 0; nodes.Position[i] = new FixedVec3(Fixed.FromRaw(px[i]), Fixed.FromRaw(py[i]), Fixed.FromRaw(pz[i]));
                nodes.SupplyRemaining[i] = Fixed.FromRaw(sr[i]); nodes.SupplyTotal[i] = Fixed.FromRaw(st[i]); nodes.GatherRate[i] = Fixed.FromRaw(gr[i]);
                nodes.MaxGatherers[i] = mg[i]; nodes.AssignedGatherers[i] = ag[i]; nodes.CollectionModel[i] = (ResourceCollectionModel)cm[i];
                nodes.ResourceType[i] = (ResourceKind)rt[i]; nodes.RequiresStructureRadius[i] = Fixed.FromRaw(rsr[i]);
                nodes.OwnerFaction[i] = (Faction)of[i]; nodes.IncomePeriodTicks[i] = ipt[i]; nodes.IncomeTicksElapsed[i] = ite[i];
                nodes.RequiresStructureId[i] = i < NodeRequiresStructureId.Length ? NodeRequiresStructureId[i] : "";
            }
            nodes.RestoreCount(NodeCount);
        }

        private void RestoreHeroes(HeroStore h, FactionDefinition?[]? slotDefs)
        {
            int n = HeroCount;
            int[] G(HA e) => Hero[(int)e];
            var al = G(HA.Alive); var eid = G(HA.EntityId); var lv = G(HA.Level); var xp = G(HA.Xp);
            var gsa = G(HA.GrowthStacksApplied); var ml = G(HA.MaxLevelOf); var bx = G(HA.BaseXpOf); var xg = G(HA.XpGrowthOf);
            var xsr = G(HA.XpShareRadiusOf); var hpl = G(HA.HealthPerLevelOf); var dpl = G(HA.DamagePerLevelOf); var apl = G(HA.ArmorPerLevelOf);
            var a314 = G(HA.Alive3_14); var awr = G(HA.AwaitingRevival); var rti = G(HA.RevivalTimer); var rlk = G(HA.RevivalLink);
            var of = G(HA.OwnerFaction); var gen = G(HA.Generation); var inv = G(HA.Inventory);
            for (int i = 0; i < n; i++)
            {
                h.Alive[i] = al[i] != 0; h.Id[i] = new HeroId(HeroId[i]); h.EntityId[i] = eid[i]; h.Level[i] = lv[i]; h.Xp[i] = Fixed.FromRaw(xp[i]);
                h.GrowthStacksApplied[i] = gsa[i]; h.MaxLevelOf[i] = ml[i]; h.BaseXpOf[i] = Fixed.FromRaw(bx[i]); h.XpGrowthOf[i] = Fixed.FromRaw(xg[i]);
                h.XpShareRadiusOf[i] = Fixed.FromRaw(xsr[i]); h.HealthPerLevelOf[i] = Fixed.FromRaw(hpl[i]); h.DamagePerLevelOf[i] = Fixed.FromRaw(dpl[i]); h.ArmorPerLevelOf[i] = Fixed.FromRaw(apl[i]);
                h.Alive3_14[i] = a314[i] != 0; h.AwaitingRevival[i] = awr[i] != 0; h.RevivalTimer[i] = Fixed.FromRaw(rti[i]); h.RevivalLink[i] = rlk[i];
                h.OwnerFaction[i] = (Faction)of[i]; h.Generation[i] = gen[i];
                h.SourceDef[i] = ResolveDef(slotDefs, of[i], i < HeroDefId.Length ? HeroDefId[i] : "");
            }
            for (int i = 0; i < n * HeroStore.INVENTORY_SLOTS && i < inv.Length; i++) h.Inventory[i] = inv[i];
            h.RestoreManagement(HeroCount, HeroFreeList, HeroFreeList.Length);
        }

        private void RestoreItems(ItemStore it)
        {
            int n = ItemCount;
            int[] G(IA e) => Item[(int)e];
            var al = G(IA.Alive); var df = G(IA.DefId); var ch = G(IA.Charges); var px = G(IA.PosX); var pz = G(IA.PosZ);
            var hd = G(IA.Held); var cs = G(IA.CarrierHeroSlot); var gen = G(IA.Generation);
            for (int i = 0; i < n; i++)
            {
                it.Alive[i] = al[i] != 0; it.DefId[i] = df[i]; it.Charges[i] = ch[i]; it.PosX[i] = Fixed.FromRaw(px[i]); it.PosZ[i] = Fixed.FromRaw(pz[i]);
                it.Held[i] = hd[i] != 0; it.CarrierHeroSlot[i] = cs[i]; it.Generation[i] = gen[i];
            }
            it.RestoreManagement(ItemCount, ItemFreeList, ItemFreeList.Length);
        }

        private void RestoreProjectiles(ProjectileStore p)
        {
            int n = ProjHwm;
            int[] G(PA e) => Proj[(int)e];
            var al = G(PA.Alive); var px = G(PA.PosX); var py = G(PA.PosY); var pz = G(PA.PosZ); var ti = G(PA.TargetId);
            var lx = G(PA.LastKnownX); var ly = G(PA.LastKnownY); var lz = G(PA.LastKnownZ); var dm = G(PA.Damage);
            var dty = G(PA.DmgType); var ta = G(PA.TargetArmor); var ow = G(PA.Owner); var sr = G(PA.SplashRadius);
            var sp = G(PA.Speed); var tib = G(PA.TargetIsBuilding); var sid = G(PA.SourceId);
            for (int i = 0; i < n; i++)
            {
                p.Alive[i] = al[i] != 0; p.Position[i] = new FixedVec3(Fixed.FromRaw(px[i]), Fixed.FromRaw(py[i]), Fixed.FromRaw(pz[i]));
                p.TargetId[i] = ti[i]; p.LastKnownPos[i] = new FixedVec3(Fixed.FromRaw(lx[i]), Fixed.FromRaw(ly[i]), Fixed.FromRaw(lz[i]));
                p.Damage[i] = Fixed.FromRaw(dm[i]); p.DmgType[i] = (DamageType)dty[i]; p.TargetArmor[i] = (ArmorType)ta[i]; p.Owner[i] = (Faction)ow[i];
                p.SplashRadius[i] = Fixed.FromRaw(sr[i]); p.Speed[i] = Fixed.FromRaw(sp[i]); p.TargetIsBuilding[i] = tib[i] != 0; p.SourceId[i] = sid[i];
                p.Feedback[i] = null; // presentation-only, unfolded — re-derived on next impact; safe to drop
            }
            p.RestoreManagement(ProjHwm, ProjFreeList, ProjFreeList.Length);
        }

        private void RestoreResearch(ResearchStore r)
        {
            // Review fix: bound by every per-faction lane, not InProgressIndex alone (see RestoreResources).
            int f = Math.Min(r.InProgressIndex.Length, ReschInProgress.Length);
            f = Math.Min(f, Math.Min(ReschRemaining.Length, ReschCompleted.Length));
            f = Math.Min(f, Math.Min(ReschStartedX.Length, Math.Min(ReschStartedY.Length, ReschStartedZ.Length)));
            f = Math.Min(f, Math.Min(ReschCumHp.Length, Math.Min(ReschCumAtk.Length,
                                     Math.Min(ReschCumMove.Length, ReschCumArmor.Length))));
            for (int i = 0; i < f; i++)
            {
                int m = ReschCompleted[i].Length;
                m = Math.Min(m, Math.Min(ReschCumHp[i].Length, Math.Min(ReschCumAtk[i].Length,
                                         Math.Min(ReschCumMove[i].Length, ReschCumArmor[i].Length))));
                r.EnsureCapacity((Faction)i, m);
                r.InProgressIndex[i] = ReschInProgress[i]; r.RemainingTicks[i] = ReschRemaining[i];
                r.StartedAtPosition[i] = new FixedVec3(Fixed.FromRaw(ReschStartedX[i]), Fixed.FromRaw(ReschStartedY[i]), Fixed.FromRaw(ReschStartedZ[i]));
                int mm = Math.Min(m, r.CompletedLevels[i].Length);
                for (int k = 0; k < mm; k++)
                {
                    r.CompletedLevels[i][k] = ReschCompleted[i][k];
                    r.CumulativeMaxHealthDelta[i][k] = Fixed.FromRaw(ReschCumHp[i][k]);
                    r.CumulativeAttackDamageDelta[i][k] = Fixed.FromRaw(ReschCumAtk[i][k]);
                    r.CumulativeMoveSpeedDelta[i][k] = Fixed.FromRaw(ReschCumMove[i][k]);
                    r.CumulativeArmorDelta[i][k] = Fixed.FromRaw(ReschCumArmor[i][k]);
                }
            }
        }

        private void RestoreWinState(WinStateStore w)
        {
            w.MatchTicks = WinMatchTicks;
            // Review fix: bound by every lane read in the loop, not KothHoldTicks alone (see RestoreResources).
            int f = Math.Min(w.KothHoldTicks.Length, WinKoth.Length);
            f = Math.Min(f, Math.Min(WinSurvival.Length, WinVerdict.Length));
            for (int i = 0; i < f; i++) { w.KothHoldTicks[i] = WinKoth[i]; w.SurvivalRemaining[i] = WinSurvival[i]; w.Verdict[i] = WinVerdict[i]; }
        }

        private void RestoreAlliances(AllianceStore a)
        {
            int f = Math.Min(a.TeamId.Length, AllianceTeam.Length);
            for (int i = 0; i < f; i++) a.TeamId[i] = AllianceTeam[i];
        }

        private void RestoreTriggerEnabled(TriggerEnabledStore t)
        {
            int n = Math.Min(t.Count, TriggerEnabled.Length);
            for (int i = 0; i < n; i++) t.Set(i, TriggerEnabled[i] != 0);
        }

        private void RestoreModifiers(ModifierStore m, CanonicalEffectDescriptorTable table)
        {
            // Wipe the re-applied self-passives + accumulators first (StatusFlagsOf was already set by the entity overlay).
            m.Clear();
            // Rebuild [0,count) per entity in ascending host-id then slot order (the capture order).
            int lastHost = -1, slot = 0;
            for (int i = 0; i < Modifiers.Length; i++)
            {
                var e = Modifiers[i];
                if (e.HostId != lastHost) { if (lastHost >= 0) m.SetCount(lastHost, slot); lastHost = e.HostId; slot = 0; }
                Modifier? mod = e.Kind == 0 ? table.GetModifier(e.DescIndex) : null;
                PersistentEffect? pe = e.Kind == 1 ? table.GetPersistent(e.DescIndex) : null;
                if (mod == null && pe == null)
                    throw new InvalidDataException("SP load: a saved modifier/persistent descriptor index is out of range (corrupt save or content drift).");
                m.RestoreSlot(e.HostId, slot, e.ModifierId, e.RemainingTicks, e.TicksUntilPeriod, e.PeriodsRemaining,
                              e.StackCount, e.CasterId, (Faction)e.CasterFaction, mod, pe);
                slot++;
            }
            if (lastHost >= 0) m.SetCount(lastHost, slot);
        }

        private void RestoreDirector(ScenarioDirector d, EntityWorld w)
        {
            var fired = new bool[DirFired.Length];
            for (int i = 0; i < DirFired.Length; i++) fired[i] = DirFired[i] != 0;
            d.RestoreRuntimeState(fired, DirCooldown, DirFirstTick != 0);

            // Review fix: LoadScenario seeded the director's change-detection snapshots from the AUTHORED board; the
            // entity/building stores above now hold the SAVED match. Re-derive the snapshots from the restored world
            // or the first resumed tick emits a spurious building_completed / unit_dies edge for every divergence,
            // re-firing non-run_once triggers into folded state. Must stay AFTER RestoreEntities/RestoreBuildings.
            d.ReseedChangeDetection(w);
        }

        private void RestoreLoopState(DslLoopState l)
        {
            // Rows were (re)configured by the load path's LoadScenario (ConfigureRows). Overlay the drain state by index.
            int n = Math.Min(l.RowCount, LoopRowActive.Length);
            for (int r = 0; r < n; r++)
            {
                if (LoopRowActive[r] != 0)
                {
                    l.BeginSnapshot(r);
                    int len = LoopRowLen[r];
                    for (int k = 0; k < len; k++) l.SnapshotAppend(r, LoopRowIds[r][k]);
                    l.SetCursor(r, LoopRowCursor[r]);
                }
            }
            l.ResetFuel();
            if (LoopFuel > 0) l.Charge(LoopFuel);
        }

        private void RestoreDslEvents(DslEventQueue q)
        {
            q.Clear();
            for (int i = 0; i < DslEvIndex.Length; i++)
                q.Enqueue(DslEvIndex[i], DslEvRaiser[i], DslEvParams[i], DslEvParams[i].Length);
        }

        private void RestoreAi(AiOpponentSystem ai) => ai.RestoreSaveState(AiProductionIds, AiCmdCenterExpId, AiAttackCooldownRaw);

        // ═══════════════════════════════════ BODY SERIALIZATION ═══════════════════════════════════
        // Length-framed, type-tagged sections terminated by a zero-length frame. The reader throws InvalidDataException
        // on an unknown section tag or truncation (never a silent partial load — the ReplayPlayer precedent).

        private enum Tag : byte
        {
            Scalars = 1, Entities = 2, Buildings = 3, Resources = 4, Nodes = 5, Heroes = 6, Items = 7, Projectiles = 8,
            Research = 9, WinState = 10, Alliances = 11, TriggerEnabled = 12, Modifiers = 13, Director = 14,
            DslVars = 15, LoopState = 16, DslEvents = 17, Ai = 18, MatchStats = 19,
        }

        // Every section is always written; ReadBody requires all of them present (a missing section would silently
        // restore that store to its default and desync — #4 fail-closed). Keep in sync with WriteBody/the Tag enum.
        private const int FirstTag = (int)Tag.Scalars;
        private const int LastTag  = (int)Tag.MatchStats;

        /// <summary>Write the body: one framed section per store, terminated by a zero-length frame.</summary>
        public void WriteBody(BinaryWriter w)
        {
            Frame(w, Tag.Scalars, b => { b.Write(Tick); b.Write(RngState); });
            Frame(w, Tag.Entities, b =>
            {
                b.Write(EntHwm); b.Write(EntAliveCount); WI(b, EntFreeList); WJ(b, Ent); WS(b, EntDefId);
            });
            Frame(w, Tag.Buildings, b => { b.Write(BCount); WI(b, BFreeList); WJ(b, Bld); WS(b, BDefinitionId); WSJ(b, BShopStock); });
            Frame(w, Tag.Resources, b => { WI(b, ResOre); WI(b, ResCrystal); WI(b, ResSupplyUsed); WI(b, ResSupplyCap); WI(b, ResBaseX); WI(b, ResBaseY); WI(b, ResBaseZ); });
            Frame(w, Tag.Nodes, b => { b.Write(NodeCount); WJ(b, Node); WS(b, NodeRequiresStructureId); });
            Frame(w, Tag.Heroes, b => { b.Write(HeroCount); WI(b, HeroFreeList); WJ(b, Hero); WU(b, HeroId); WS(b, HeroDefId); });
            Frame(w, Tag.Items, b => { b.Write(ItemCount); WI(b, ItemFreeList); WJ(b, Item); });
            Frame(w, Tag.Projectiles, b => { b.Write(ProjHwm); WI(b, ProjFreeList); WJ(b, Proj); });
            Frame(w, Tag.Research, b =>
            {
                WI(b, ReschInProgress); WI(b, ReschRemaining); WI(b, ReschStartedX); WI(b, ReschStartedY); WI(b, ReschStartedZ);
                WJ(b, ReschCompleted); WJ(b, ReschCumHp); WJ(b, ReschCumAtk); WJ(b, ReschCumMove); WJ(b, ReschCumArmor);
            });
            Frame(w, Tag.WinState, b => { b.Write(WinMatchTicks); WI(b, WinKoth); WI(b, WinSurvival); WI(b, WinVerdict); });
            Frame(w, Tag.Alliances, b => WI(b, AllianceTeam));
            Frame(w, Tag.TriggerEnabled, b => WI(b, TriggerEnabled));
            Frame(w, Tag.Modifiers, b =>
            {
                b.Write(Modifiers.Length);
                for (int i = 0; i < Modifiers.Length; i++)
                {
                    var e = Modifiers[i];
                    b.Write(e.HostId); b.Write(e.Kind); b.Write(e.DescIndex); b.Write(e.ModifierId); b.Write(e.RemainingTicks);
                    b.Write(e.TicksUntilPeriod); b.Write(e.PeriodsRemaining); b.Write(e.StackCount); b.Write(e.CasterId); b.Write(e.CasterFaction);
                }
            });
            Frame(w, Tag.Director, b => { WI(b, DirFired); WI(b, DirCooldown); b.Write(DirFirstTick); });
            Frame(w, Tag.DslVars, b =>
            {
                WS(b, DslVars.GlobalNames); WI(b, DslVars.GlobalTypes); WI(b, DslVars.GlobalRaw0); WI(b, DslVars.GlobalRaw1);
                WI(b, DslVars.PerPlayerRaw0); WI(b, DslVars.PerPlayerRaw1); WI(b, DslVars.ArrayCounts); WJ(b, DslVars.ArrayRaws);
                WS(b, DslVars.TimerNames); WI(b, DslVars.TimerRemaining);
            });
            Frame(w, Tag.LoopState, b => { b.Write(LoopFuel); WI(b, LoopRowActive); WI(b, LoopRowCursor); WI(b, LoopRowLen); WJ(b, LoopRowIds); });
            Frame(w, Tag.DslEvents, b => { WI(b, DslEvIndex); WI(b, DslEvRaiser); WJ(b, DslEvParams); });
            Frame(w, Tag.Ai, b => { WI(b, AiProductionIds); b.Write(AiCmdCenterExpId); b.Write(AiAttackCooldownRaw); });
            Frame(w, Tag.MatchStats, b => WJ(b, Stats));
            w.Write(0); // zero-length frame terminator
        }

        /// <summary>Read the body written by <see cref="WriteBody"/>, fail-closed on an unknown tag or truncation.</summary>
        public static SaveGameState ReadBody(BinaryReader r, string ctx)
        {
            var s = new SaveGameState();
            var seen = new bool[LastTag + 1];
            while (true)
            {
                int len;
                try { len = r.ReadInt32(); }
                catch (EndOfStreamException) { throw new InvalidDataException($"Save '{ctx}': truncated body (missing section frame)."); }
                if (len == 0) break;
                if (len < 0) throw new InvalidDataException($"Save '{ctx}': negative section length.");
                byte[] bytes;
                try { bytes = r.ReadBytes(len); }
                catch (EndOfStreamException) { throw new InvalidDataException($"Save '{ctx}': truncated section body."); }
                if (bytes.Length < len) throw new InvalidDataException($"Save '{ctx}': truncated section body.");
                using var ms = new MemoryStream(bytes);
                using var br = new BinaryReader(ms);
                var tag = (Tag)br.ReadByte();
                try { s.ReadSection(tag, br, ctx); }
                catch (EndOfStreamException) { throw new InvalidDataException($"Save '{ctx}': truncated section '{tag}'."); }
                if ((int)tag >= FirstTag && (int)tag <= LastTag) seen[(int)tag] = true;
            }
            // #4 fail-closed: every section must be present — a missing one would silently restore a store to defaults.
            for (int t = FirstTag; t <= LastTag; t++)
                if (!seen[t]) throw new InvalidDataException($"Save '{ctx}': missing required section '{(Tag)t}' — corrupt save.");
            // #2 fail-closed: cross-array/scalar consistency (counts ≤ caps, lane cardinality, no null lanes) BEFORE any
            // RestoreInto mutates a host, so a malformed-but-parseable save is rejected here instead of detonating later.
            s.Validate(ctx);
            return s;
        }

        /// <summary>#2 fail-closed structural validation over the parsed state, run after <see cref="ReadBody"/> and
        /// BEFORE any host is mutated. Throws <see cref="InvalidDataException"/> on any count-over-cap, wrong lane
        /// cardinality, null lane, cross-array length mismatch, or out-of-range element that would otherwise crash
        /// (IndexOutOfRange/NRE) inside restore.</summary>
        public void Validate(string ctx)
        {
            void Fail(string why) => throw new InvalidDataException($"Save '{ctx}': {why}");

            // Review fix: the length checks below bounded each free list but never its ELEMENTS, so a corrupt or
            // hand-edited save carrying an out-of-range or duplicated slot id passed the whole fail-closed gate and
            // detonated later — Create() pops _freeList[--_freeCount] and writes at that index (IndexOutOfRange), or a
            // duplicate hands one slot to two spawns. Validate the contents here, where the posture says they belong.
            void FreeList(string name, int[] list, int cap)
            {
                if (list == null) { Fail($"{name} free-list is null."); return; }
                if (list.Length > cap) Fail($"{name} free-list too long.");
                var seen = new bool[cap];
                for (int i = 0; i < list.Length; i++)
                {
                    int slot = list[i];
                    if ((uint)slot >= (uint)cap) Fail($"{name} free-list entry {i} = {slot} is outside [0, {cap}).");
                    if (seen[slot]) Fail($"{name} free-list contains slot {slot} more than once.");
                    seen[slot] = true;
                }
            }

            // ── EntityWorld ──
            if ((uint)EntHwm > EntityWorld.MAX_ENTITIES) Fail($"entity count {EntHwm} exceeds cap {EntityWorld.MAX_ENTITIES}.");
            if (EntAliveCount < 0 || EntAliveCount > EntityWorld.MAX_ENTITIES) Fail($"alive count {EntAliveCount} out of range.");
            FreeList("entity", EntFreeList, EntityWorld.MAX_ENTITIES);
            if (Ent.Length != (int)EA.COUNT) Fail("entity lane count mismatch.");
            for (int e = 0; e < Ent.Length; e++) if (Ent[e] == null) Fail($"entity lane {e} is null.");
            for (int e = 0; e < (int)EA.PatrolWpX; e++) if (Ent[e].Length != EntHwm) Fail($"entity lane {(EA)e} length {Ent[e].Length} != {EntHwm}.");
            RequireLen((EA)(int)EA.PatrolWpX, EntHwm * EntityWorld.MAX_PATROL_WAYPOINTS, Fail);
            RequireLen(EA.PatrolWpY, EntHwm * EntityWorld.MAX_PATROL_WAYPOINTS, Fail);
            RequireLen(EA.PatrolWpZ, EntHwm * EntityWorld.MAX_PATROL_WAYPOINTS, Fail);
            RequireLen(EA.OrderQCmd, EntHwm * EntityWorld.MAX_ORDER_QUEUE, Fail);
            RequireLen(EA.OrderQTargetX, EntHwm * EntityWorld.MAX_ORDER_QUEUE, Fail);
            RequireLen(EA.OrderQTargetZ, EntHwm * EntityWorld.MAX_ORDER_QUEUE, Fail);
            RequireLen(EA.AbilityId, EntHwm * EntityWorld.MAX_ABILITIES_PER_UNIT, Fail);
            RequireLen(EA.AbilityCd, EntHwm * EntityWorld.MAX_ABILITIES_PER_UNIT, Fail);
            if (EntDefId.Length != EntHwm) Fail("entity def-id lane length mismatch.");
            // Narrowing-cast domains (int → byte/sbyte): a corrupt out-of-range value must be rejected, not truncated.
            RequireByte(EA.SupplyCost, Fail); RequireByte(EA.MeshType, Fail);
            RequireByte(EA.PatrolCount, Fail); RequireByte(EA.OrderQueueCount, Fail);
            RequireByte(EA.PatrolIndex, Fail); RequireByte(EA.ActiveOrderCmd, Fail); RequireByte(EA.AbilityCount, Fail);
            RequireSByte(EA.PatrolDir, Fail);

            // ── BuildingStore ──
            if ((uint)BCount > BuildingStore.MAX_BUILDINGS) Fail($"building count {BCount} exceeds cap.");
            if (Bld.Length != (int)BA.COUNT) Fail("building lane count mismatch.");
            for (int e = 0; e < Bld.Length; e++) { if (Bld[e] == null) Fail($"building lane {e} is null."); if (Bld[e].Length != BCount) Fail($"building lane {(BA)e} length mismatch."); }
            if (BDefinitionId.Length != BCount) Fail("building def-id lane length mismatch.");
            if (BShopStock.Length != BCount) Fail("building shop-stock lane length mismatch.");
            FreeList("building", BFreeList, BuildingStore.MAX_BUILDINGS);

            // ── ResourceStore ── (fixed FACTION_ARRAY_SIZE)
            int fa = FactionRegistry.FACTION_ARRAY_SIZE;
            if (ResOre.Length != fa || ResCrystal.Length != fa || ResSupplyUsed.Length != fa || ResSupplyCap.Length != fa
                || ResBaseX.Length != fa || ResBaseY.Length != fa || ResBaseZ.Length != fa) Fail("resource-store array length mismatch.");

            // ── ResourceNodeStore ──
            if ((uint)NodeCount > ResourceNodeStore.MAX_NODES) Fail($"node count {NodeCount} exceeds cap.");
            if (Node.Length != (int)NA.COUNT) Fail("node lane count mismatch.");
            for (int e = 0; e < Node.Length; e++) { if (Node[e] == null) Fail($"node lane {e} is null."); if (Node[e].Length != NodeCount) Fail($"node lane {(NA)e} length mismatch."); }
            if (NodeRequiresStructureId.Length != NodeCount) Fail("node requires-structure lane length mismatch.");

            // ── HeroStore ──
            if ((uint)HeroCount > HeroStore.MAX_HEROES) Fail($"hero count {HeroCount} exceeds cap.");
            if (Hero.Length != (int)HA.COUNT) Fail("hero lane count mismatch.");
            FreeList("hero", HeroFreeList, HeroStore.MAX_HEROES);
            for (int e = 0; e < Hero.Length; e++)
            {
                if (Hero[e] == null) Fail($"hero lane {e} is null.");
                int want = e == (int)HA.Inventory ? HeroCount * HeroStore.INVENTORY_SLOTS : HeroCount;
                if (Hero[e].Length != want) Fail($"hero lane {(HA)e} length mismatch.");
            }
            if (HeroId.Length != HeroCount || HeroDefId.Length != HeroCount) Fail("hero id/def-id lane length mismatch.");

            // ── ItemStore ──
            if ((uint)ItemCount > ItemStore.MAX_ITEMS) Fail($"item count {ItemCount} exceeds cap.");
            if (Item.Length != (int)IA.COUNT) Fail("item lane count mismatch.");
            for (int e = 0; e < Item.Length; e++) { if (Item[e] == null) Fail($"item lane {e} is null."); if (Item[e].Length != ItemCount) Fail($"item lane {(IA)e} length mismatch."); }
            FreeList("item", ItemFreeList, ItemStore.MAX_ITEMS);

            // ── ProjectileStore ──
            if ((uint)ProjHwm > ProjectileStore.MAX_PROJECTILES) Fail($"projectile count {ProjHwm} exceeds cap.");
            if (Proj.Length != (int)PA.COUNT) Fail("projectile lane count mismatch.");
            for (int e = 0; e < Proj.Length; e++) { if (Proj[e] == null) Fail($"projectile lane {e} is null."); if (Proj[e].Length != ProjHwm) Fail($"projectile lane {(PA)e} length mismatch."); }
            FreeList("projectile", ProjFreeList, ProjectileStore.MAX_PROJECTILES);

            // ── ResearchStore ── (per-faction outer arrays + consistent jagged inner lengths)
            if (ReschInProgress.Length != fa || ReschRemaining.Length != fa || ReschStartedX.Length != fa
                || ReschStartedY.Length != fa || ReschStartedZ.Length != fa
                || ReschCompleted.Length != fa || ReschCumHp.Length != fa || ReschCumAtk.Length != fa
                || ReschCumMove.Length != fa || ReschCumArmor.Length != fa) Fail("research outer-array length mismatch.");
            for (int i = 0; i < fa; i++)
            {
                if (ReschCompleted[i] == null) Fail($"research completed lane {i} is null.");
                int m = ReschCompleted[i].Length;
                if (ReschCumHp[i] == null || ReschCumHp[i].Length != m || ReschCumAtk[i] == null || ReschCumAtk[i].Length != m
                    || ReschCumMove[i] == null || ReschCumMove[i].Length != m || ReschCumArmor[i] == null || ReschCumArmor[i].Length != m)
                    Fail($"research inner-array length mismatch at faction {i}.");
            }

            // ── WinState / Alliances / TriggerEnabled ──
            if (WinKoth.Length != fa || WinSurvival.Length != fa || WinVerdict.Length != fa) Fail("win-state array length mismatch.");
            if (AllianceTeam.Length != fa) Fail("alliance array length mismatch.");

            // ── ModifierStore ── (host in range, per-host count ≤ cap, entries grouped ascending by host)
            int prevHost = -1, hostCount = 0;
            for (int i = 0; i < Modifiers.Length; i++)
            {
                var e = Modifiers[i];
                if ((uint)e.HostId >= EntityWorld.MAX_ENTITIES) Fail($"modifier host id {e.HostId} out of range.");
                if (e.Kind > 1 || e.DescIndex < 0) Fail("modifier entry has an invalid descriptor kind/index.");
                if (e.HostId != prevHost) { if (e.HostId < prevHost) Fail("modifier entries not grouped ascending by host."); prevHost = e.HostId; hostCount = 0; }
                if (++hostCount > EffectCaps.MaxModifiersPerEntity) Fail($"entity {e.HostId} has more than {EffectCaps.MaxModifiersPerEntity} modifiers.");
            }

            // ── DslLoopState / DslEventQueue ──
            if (LoopRowActive.Length != LoopRowCursor.Length || LoopRowActive.Length != LoopRowLen.Length || LoopRowActive.Length != LoopRowIds.Length) Fail("loop-state row-array length mismatch.");
            for (int r = 0; r < LoopRowIds.Length; r++)
            {
                if (LoopRowIds[r] == null) Fail($"loop-state row {r} ids null.");
                if ((uint)LoopRowLen[r] > (uint)LoopRowIds[r].Length) Fail($"loop-state row {r} length {LoopRowLen[r]} exceeds its id buffer.");
            }
            if (DslEvRaiser.Length < DslEvIndex.Length || DslEvParams.Length < DslEvIndex.Length) Fail("dsl-event parallel-array length mismatch.");
            for (int i = 0; i < DslEvIndex.Length; i++) if (DslEvParams[i] == null) Fail($"dsl-event param row {i} is null.");

            // ── Director ──
            if (DirCooldown == null || DirFired == null) Fail("director runtime arrays null.");
        }

        private void RequireLen(EA e, int want, Action<string> fail) { if (Ent[(int)e].Length != want) fail($"entity lane {e} length {Ent[(int)e].Length} != {want}."); }
        private void RequireByte(EA e, Action<string> fail) { var a = Ent[(int)e]; for (int i = 0; i < a.Length; i++) if ((uint)a[i] > byte.MaxValue) fail($"entity lane {e}[{i}] value {a[i]} out of byte range."); }
        private void RequireSByte(EA e, Action<string> fail) { var a = Ent[(int)e]; for (int i = 0; i < a.Length; i++) if (a[i] < sbyte.MinValue || a[i] > sbyte.MaxValue) fail($"entity lane {e}[{i}] value {a[i]} out of sbyte range."); }

        private void ReadSection(Tag tag, BinaryReader b, string ctx)
        {
            switch (tag)
            {
                case Tag.Scalars: Tick = b.ReadUInt32(); RngState = b.ReadUInt64(); break;
                case Tag.Entities: EntHwm = b.ReadInt32(); EntAliveCount = b.ReadInt32(); EntFreeList = RI(b); Ent = RJ(b); EntDefId = RS(b); break;
                case Tag.Buildings: BCount = b.ReadInt32(); BFreeList = RI(b); Bld = RJ(b); BDefinitionId = RS(b); BShopStock = RSJ(b); break;
                case Tag.Resources: ResOre = RI(b); ResCrystal = RI(b); ResSupplyUsed = RI(b); ResSupplyCap = RI(b); ResBaseX = RI(b); ResBaseY = RI(b); ResBaseZ = RI(b); break;
                case Tag.Nodes: NodeCount = b.ReadInt32(); Node = RJ(b); NodeRequiresStructureId = RS(b); break;
                case Tag.Heroes: HeroCount = b.ReadInt32(); HeroFreeList = RI(b); Hero = RJ(b); HeroId = RU(b); HeroDefId = RS(b); break;
                case Tag.Items: ItemCount = b.ReadInt32(); ItemFreeList = RI(b); Item = RJ(b); break;
                case Tag.Projectiles: ProjHwm = b.ReadInt32(); ProjFreeList = RI(b); Proj = RJ(b); break;
                case Tag.Research:
                    ReschInProgress = RI(b); ReschRemaining = RI(b); ReschStartedX = RI(b); ReschStartedY = RI(b); ReschStartedZ = RI(b);
                    ReschCompleted = RJ(b); ReschCumHp = RJ(b); ReschCumAtk = RJ(b); ReschCumMove = RJ(b); ReschCumArmor = RJ(b); break;
                case Tag.WinState: WinMatchTicks = b.ReadInt32(); WinKoth = RI(b); WinSurvival = RI(b); WinVerdict = RI(b); break;
                case Tag.Alliances: AllianceTeam = RI(b); break;
                case Tag.TriggerEnabled: TriggerEnabled = RI(b); break;
                case Tag.Modifiers:
                {
                    int n = b.ReadInt32();
                    if (n < 0) throw new InvalidDataException($"Save '{ctx}': negative modifier count.");
                    Modifiers = new ModifierEntry[n];
                    for (int i = 0; i < n; i++)
                        Modifiers[i] = new ModifierEntry
                        {
                            HostId = b.ReadInt32(), Kind = b.ReadByte(), DescIndex = b.ReadInt32(), ModifierId = b.ReadInt32(),
                            RemainingTicks = b.ReadInt32(), TicksUntilPeriod = b.ReadInt32(), PeriodsRemaining = b.ReadInt32(),
                            StackCount = b.ReadInt32(), CasterId = b.ReadInt32(), CasterFaction = b.ReadInt32(),
                        };
                    break;
                }
                case Tag.Director: DirFired = RI(b); DirCooldown = RI(b); DirFirstTick = b.ReadInt32(); break;
                case Tag.DslVars:
                    DslVars = new DslVarTable.Snapshot
                    {
                        GlobalNames = RS(b), GlobalTypes = RI(b), GlobalRaw0 = RI(b), GlobalRaw1 = RI(b),
                        PerPlayerRaw0 = RI(b), PerPlayerRaw1 = RI(b), ArrayCounts = RI(b), ArrayRaws = RJ(b),
                        TimerNames = RS(b), TimerRemaining = RI(b),
                    };
                    break;
                case Tag.LoopState: LoopFuel = b.ReadInt32(); LoopRowActive = RI(b); LoopRowCursor = RI(b); LoopRowLen = RI(b); LoopRowIds = RJ(b); break;
                case Tag.DslEvents: DslEvIndex = RI(b); DslEvRaiser = RI(b); DslEvParams = RJ(b); break;
                case Tag.Ai: AiProductionIds = RI(b); AiCmdCenterExpId = b.ReadInt32(); AiAttackCooldownRaw = b.ReadInt32(); break;
                case Tag.MatchStats: Stats = RJ(b); break;
                default: throw new InvalidDataException($"Save '{ctx}': unknown section tag 0x{(byte)tag:X2} — corrupt or newer-format save.");
            }
        }

        // ── framing + primitive array helpers ──
        private static void Frame(BinaryWriter w, Tag tag, Action<BinaryWriter> body)
        {
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((byte)tag);
                body(bw);
                bw.Flush();
                byte[] bytes = ms.ToArray();
                w.Write(bytes.Length);
                w.Write(bytes);
            }
        }

        private static void WI(BinaryWriter w, int[] a) { w.Write(a.Length); for (int i = 0; i < a.Length; i++) w.Write(a[i]); }
        private static int[] RI(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) throw new InvalidDataException("negative array length"); var a = new int[n]; for (int i = 0; i < n; i++) a[i] = r.ReadInt32(); return a; }
        private static void WU(BinaryWriter w, ulong[] a) { w.Write(a.Length); for (int i = 0; i < a.Length; i++) w.Write(a[i]); }
        private static ulong[] RU(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) throw new InvalidDataException("negative array length"); var a = new ulong[n]; for (int i = 0; i < n; i++) a[i] = r.ReadUInt64(); return a; }
        private static void WJ(BinaryWriter w, int[][] a) { w.Write(a.Length); for (int i = 0; i < a.Length; i++) WI(w, a[i] ?? Array.Empty<int>()); }
        private static int[][] RJ(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) throw new InvalidDataException("negative jagged length"); var a = new int[n][]; for (int i = 0; i < n; i++) a[i] = RI(r); return a; }
        private static void WS(BinaryWriter w, string[] a) { w.Write(a.Length); for (int i = 0; i < a.Length; i++) w.Write(a[i] ?? ""); }
        private static string[] RS(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) throw new InvalidDataException("negative string-array length"); var a = new string[n]; for (int i = 0; i < n; i++) a[i] = r.ReadString(); return a; }
        private static void WSJ(BinaryWriter w, string[][] a) { w.Write(a.Length); for (int i = 0; i < a.Length; i++) WS(w, a[i] ?? Array.Empty<string>()); }
        private static string[][] RSJ(BinaryReader r) { int n = r.ReadInt32(); if (n < 0) throw new InvalidDataException("negative jagged length"); var a = new string[n][]; for (int i = 0; i < n; i++) a[i] = RS(r); return a; }
    }
}
