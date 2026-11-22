namespace Styx.PartyGrind
{
    /// <summary>
    /// Squad role assigned to a PartyGrind client.
    /// </summary>
    public enum SquadRole
    {
        TankLeader = 0,
        Healer = 1,
        DpsMelee = 2,
        DpsRanged = 3,
        HybridOffHeal = 4
    }

    public enum SquadBotMode
    {
        OpenWorldGrind = 0,
        Dungeon = 1
    }

    public enum SquadDungeonEntryMode
    {
        LfgRandomNormal = 0,
        LfgRandomHeroic = 1,
        LfgSpecificByBracket = 2,
        WalkInPortalFarm = 3
    }

    public enum SquadDungeonLootMode
    {
        BossesOnly = 0,
        AllTrashAndBosses = 1
    }

    public enum SquadPhase
    {
        Initializing = 0,
        MigratingZone = 1,
        Grinding = 2,
        Resting = 3,
        Provisioning = 4,
        Looting = 5,
        TownRun = 6,
        WaitingResurrection = 7,
        CorpseRun = 8,
        Paused = 9,
        DungeonQueueing = 10,
        DungeonTravelToPortal = 11,
        DungeonClearing = 12,
        DungeonCompleteExit = 13,
        DungeonLockoutWait = 14
    }

    /// <summary>
    /// Party loot distribution method enforced by the coordinator.
    /// </summary>
    public enum SquadLootMethod
    {
        FreeForAll = 0,
        GroupLoot = 1
    }

    /// <summary>
    /// Automated roll rule used on START_LOOT_ROLL events.
    /// Values 0..3 match WoW 3.3.5a RollOnLoot(rollId, rollType).
    /// </summary>
    public enum SquadRollRule
    {
        Pass = 0,
        Need = 1,
        Greed = 2,
        Disenchant = 3,
        SmartUpgrade = 4
    }

    /// <summary>
    /// Controls when the squad mounts during travel.
    /// </summary>
    public enum SquadMountPolicy
    {
        SyncWithSlowestMember = 0,
        MountAndWaitAtCheckpoints = 1,
        NeverMount = 2
    }

    /// <summary>
    /// Controls how EffectiveSquadMinLevel advances across sub-profiles.
    /// </summary>
    public enum SquadLevelAdvanceMode
    {
        WaitForLowestMember = 0
    }
}
