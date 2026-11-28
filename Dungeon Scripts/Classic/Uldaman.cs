using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CommonBehaviors.Actions;
using Styx;
using Styx.Logic;
using Styx.Logic.Inventory.Frames.LootFrame;
using Styx.Logic.Pathing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

#if USE_DUNGEONBUDDY_DLL
using Bots.DungeonBuddyDll;
using Bots.DungeonBuddyDll.Profiles;
using Bots.DungeonBuddyDll.Attributes;
using Bots.DungeonBuddyDll.Helpers;
namespace Bots.DungeonBuddyDll.Dungeon_Scripts.Classic
#else
using Bots.DungeonBuddy.Profiles;
using Bots.DungeonBuddy.Attributes;
using Bots.DungeonBuddy.Helpers;
namespace Bots.DungeonBuddy.Dungeon_Scripts.Classic
#endif
{
    public class Uldaman : Dungeon
    {
        private const uint BaelogsChestEntry = 123329;
        private const uint HallOfCraftersDoorEntry = 124367;
        private const uint KeystoneEntry = 124371;
        private const uint AltarOfTheKeepersEntry = 130511;
        private const uint AltarOfArchaedasEntry = 133234;
        private const uint AncientTreasureEntry = 141979;

        private const uint IronayaEntry = 7228;
        private const uint ArchaedasEntry = 2748;
        private const uint BaelogEntry = 6906;
        private const uint EricEntry = 6907;
        private const uint OlafEntry = 6908;

        private const uint StaffOfPrehistoriaEntry = 7733;
        private const uint GnikivMedallionEntry = 7740;
        private const uint ShaftOfTsolEntry = 7741;

        private bool _keystoneTriggered;
        private bool _ironayaTriggered;

        #region Overrides of Dungeon
        public override uint DungeonId
        {
            get { return 22; }
        }

        public override WoWPoint Entrance { get { return new WoWPoint(-6063.326, -2955.967, 209.7707); } }

        public override void OnEnter()
        {
            _keystoneTriggered = false;
            _ironayaTriggered = false;
        }

        public override void OnExit()
        {
            _keystoneTriggered = false;
            _ironayaTriggered = false;
        }

        public override void IncludeTargetsFilter(List<WoWObject> incomingunits, HashSet<WoWObject> outgoingunits)
        {
            foreach (var unit in incomingunits.Select(obj => obj.ToUnit()))
            {
                if (unit == null)
                    continue;
                switch (unit.Entry)
                {
                    case 3560: // healing totem
                    case 6017: // Lava sprout totem
                        outgoingunits.Add(unit);
                        break;
                }
            }
        }

        public override void WeighTargetsFilter(List<Styx.Logic.Targeting.TargetPriority> units)
        {
            foreach (var p in units)
            {
                WoWUnit unit = p.Object.ToUnit();
                if (unit == null)
                    continue;
                switch (unit.Entry)
                {
                    case 3560: // healing totem
                    case 6017: // Lava sprout totem
                        p.Score += 100;
                        break;
                }
            }
        }

        #endregion

        [ObjectHandler(123329, "Baelog's Chest")]
        public Composite BaelogsChest()
        {
            return new PrioritySelector(
                new Decorator(
                    ctx => LootFrame.Instance.IsVisible &&
                           LootFrame.Instance.LootingObjectGuid == ((WoWGameObject)ctx).Guid &&
                           ((WoWGameObject)ctx).DistanceSqr <= 36.0,
                    new Action(ctx =>
                    {
                        LootFrame.Instance.LootAll();
                        return RunStatus.Success;
                    })),
                new Decorator(
                    ctx => StyxWoW.Me != null &&
                           !StyxWoW.Me.Combat &&
                           StyxWoW.Me.IsLeader() &&
                           !AreLostDwarvesAliveNearby() &&
                           !HasBagItem(GnikivMedallionEntry) &&
                           !HasBagItem(StaffOfPrehistoriaEntry) &&
                           ((WoWGameObject)ctx).CanLoot &&
                           !LootTargeting.Instance.AlreadyLooted.ContainsKey(((WoWGameObject)ctx).Guid),
                    ScriptHelpers.CreateInteractWithObject(BaelogsChestEntry, 3)));
        }

        [ObjectHandler(124371, "Keystone")]
        public Composite KeystoneObject()
        {
            return new Decorator(
                ctx => StyxWoW.Me != null &&
                       !StyxWoW.Me.Combat &&
                       !_keystoneTriggered &&
                       !IsBossDeadOrPresent(IronayaEntry) &&
                       ((WoWGameObject)ctx).Locked == false,
                CreateSingleUseObjectInteract(
                    () => ObjectManager.GetObjectsOfType<WoWGameObject>()
                        .Where(o => o.IsValid && o.Entry == KeystoneEntry && !o.Locked)
                        .OrderBy(o => o.DistanceSqr)
                        .FirstOrDefault(),
                    0,
                    obj => (obj != null && obj.IsValid && (obj.Locked || obj.State == WoWGameObjectState.Active)) ||
                           ObjectManager.GetObjectsOfType<WoWGameObject>().Any(o =>
                               o != null && o.IsValid && o.Entry == KeystoneEntry && (o.Locked || o.State == WoWGameObjectState.Active)) ||
                           IsBossDeadOrPresent(IronayaEntry),
                    () => { _keystoneTriggered = true; }));
        }

        [ObjectHandler(130511, "Altar of The Keepers")]
        public Composite AltarofTheKeepers()
        {
            WoWGameObject door = null;
            return new PrioritySelector(
                ctx =>
                {
                    door = HallOfCraftersDoor;
                    return ctx;
                },
                new Decorator(
                    ctx => CanPrepareOrActivateAltar(door),
                    new PrioritySelector(
                        new Decorator(
                            ctx => !HasBagItem(StaffOfPrehistoriaEntry) &&
                                   HasBagItem(GnikivMedallionEntry) &&
                                   HasBagItem(ShaftOfTsolEntry),
                            new Action(ctx =>
                            {
                                if (!TryCombineStaffOfPrehistoria())
                                    return RunStatus.Failure;
                                return HasBagItem(StaffOfPrehistoriaEntry) ? RunStatus.Success : RunStatus.Running;
                            })),
                        new Decorator(
                            ctx => HasBagItem(StaffOfPrehistoriaEntry),
                            CreateSingleUseObjectInteract(
                                () => ObjectManager.GetObjectsOfType<WoWGameObject>()
                                    .Where(o => o.IsValid && o.Entry == AltarOfTheKeepersEntry)
                                    .OrderBy(o => o.DistanceSqr)
                                    .FirstOrDefault(),
                                12,
                                obj =>
                                {
                                    WoWGameObject currentDoor = HallOfCraftersDoor;
                                    return (obj != null && obj.IsValid && obj.State == WoWGameObjectState.Active) ||
                                           (currentDoor != null && currentDoor.IsValid && currentDoor.State == WoWGameObjectState.Active) ||
                                           IsBossDeadOrPresent(IronayaEntry);
                                },
                                () => { _ironayaTriggered = true; })))));
        }

        [ObjectHandler(133234, "Altar of Archaedas")]
        public Composite AltarofTheArchaedas()
        {
            return new Decorator(
                ctx => StyxWoW.Me != null &&
                       !StyxWoW.Me.Combat &&
                       Math.Abs(((WoWGameObject)ctx).Location.Z - StyxWoW.Me.Location.Z) < 10 &&
                       BossManager.CurrentBoss != null &&
                       BossManager.CurrentBoss.Entry == ArchaedasEntry &&
                       !IsBossAwake(ArchaedasEntry),
                ScriptHelpers.CreateInteractWithObject(AltarOfArchaedasEntry, 12));
        }

        [ObjectHandler(141979, "Ancient Treasure")]
        public Composite AncientTreasure()
        {
            return new Decorator(
                ctx => BossManager.CurrentBoss == null && ((WoWGameObject)ctx).CanLoot,
                ScriptHelpers.CreateInteractWithObject(AncientTreasureEntry, 4));
        }

        WoWGameObject HallOfCraftersDoor
        {
            get { return ObjectManager.GetObjectsOfType<WoWGameObject>().FirstOrDefault(o => o.IsValid && o.Entry == HallOfCraftersDoorEntry); }
        }

        private bool CanPrepareOrActivateAltar(WoWGameObject door)
        {
            if (StyxWoW.Me == null || StyxWoW.Me.Combat || _ironayaTriggered)
                return false;
            if (IsBossDeadOrPresent(IronayaEntry))
                return false;
            if (door != null && door.State == WoWGameObjectState.Active)
                return false;
            return HasBagItem(StaffOfPrehistoriaEntry) ||
                   (HasBagItem(GnikivMedallionEntry) && HasBagItem(ShaftOfTsolEntry));
        }

        private static bool HasBagItem(uint entry)
        {
            return StyxWoW.Me != null &&
                   StyxWoW.Me.BagItems.Any(i => i != null && i.IsValid && i.Entry == entry && i.StackCount > 0);
        }

        private static bool TryCombineStaffOfPrehistoria()
        {
            if (HasBagItem(StaffOfPrehistoriaEntry))
                return true;

            if (StyxWoW.Me == null)
                return false;

            WoWItem medallion = StyxWoW.Me.BagItems.FirstOrDefault(i => i != null && i.IsValid && i.Entry == GnikivMedallionEntry && i.StackCount > 0);
            WoWItem shaft = StyxWoW.Me.BagItems.FirstOrDefault(i => i != null && i.IsValid && i.Entry == ShaftOfTsolEntry && i.StackCount > 0);
            if (medallion == null || shaft == null)
                return false;

            medallion.UseContainerItem();
            return true;
        }

        private static bool IsBossDeadOrPresent(uint bossEntry)
        {
            return BossManager.Bosses.Any(b => b.EntryId == bossEntry && b.IsDead) ||
                   ObjectManager.GetObjectsOfType<WoWUnit>().Any(u => u != null && u.IsValid && u.Entry == bossEntry);
        }

        private static bool IsBossAwake(uint bossEntry)
        {
            return ObjectManager.GetObjectsOfType<WoWUnit>().Any(u =>
                u != null && u.IsValid && u.Entry == bossEntry && u.IsAlive && u.Attackable && u.CanSelect);
        }

        private static bool AreLostDwarvesAliveNearby()
        {
            return ObjectManager.GetObjectsOfType<WoWUnit>().Any(u =>
                u != null && u.IsValid && u.IsAlive &&
                (u.Entry == BaelogEntry || u.Entry == EricEntry || u.Entry == OlafEntry));
        }

        private static Composite CreateSingleUseObjectInteract(
            Func<WoWGameObject> selector,
            int channelTime,
            Func<WoWGameObject, bool> isActivated,
            System.Action markCompleted)
        {
            WoWGameObject gameObject = null;
            return new PrioritySelector(
                ctx => gameObject = selector(),
                new Decorator(
                    ctx => gameObject != null && gameObject.DistanceSqr > 16.0,
                    new Action(ctx =>
                    {
                        MoveResult moveResult = Navigator.MoveTo(
                            gameObject.Location,
                            Math.Min(Navigator.PathPrecision, 3.0f),
                            "Approaching " + gameObject.Name);
                        if (moveResult == MoveResult.Failed ||
                            moveResult == MoveResult.PathGenerationFailed ||
                            moveResult == MoveResult.ReachedDestination)
                            return RunStatus.Failure;
                        return RunStatus.Running;
                    })),
                new Decorator(
                    ctx => gameObject != null && gameObject.DistanceSqr <= 16.0,
                    new Sequence(
                        new DecoratorContinue(
                            ctx => StyxWoW.Me.IsMoving,
                            new Sequence(
                                new Action(ctx => WoWMovement.MoveStop()),
                                new WaitContinue(2, ctx => !StyxWoW.Me.IsMoving, new ActionAlwaysSucceed()))),
                        new Action(ctx => gameObject.Interact()),
                        new WaitContinue(
                            2,
                            ctx => StyxWoW.Me.IsCasting || StyxWoW.Me.IsActuallyInCombat || (isActivated != null && isActivated(selector())),
                            new ActionAlwaysSucceed()),
                        new WaitContinue(
                            channelTime,
                            ctx => !StyxWoW.Me.IsCasting || StyxWoW.Me.IsActuallyInCombat,
                            new ActionAlwaysSucceed()),
                        new WaitContinue(
                            2,
                            ctx => StyxWoW.Me.IsActuallyInCombat || (isActivated != null && isActivated(selector())),
                            new ActionAlwaysSucceed()),
                        new Action(ctx =>
                        {
                            WoWGameObject current = selector();
                            if (isActivated == null || isActivated(current))
                            {
                                markCompleted();
                                return RunStatus.Success;
                            }
                            return RunStatus.Failure;
                        }))));
        }
    }
}
