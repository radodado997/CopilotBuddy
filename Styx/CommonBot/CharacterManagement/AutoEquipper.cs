#nullable disable
using Styx.Combat.CombatRoutine;
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Styx.CommonBot.CharacterManagement
{
    /// <summary>
    /// Automatic equipment evaluation for quest rewards and loot
    /// Based on simple stat evaluation for WotLK (3.3.5)
    /// </summary>
    public class AutoEquipper
    {
        private static readonly Dictionary<WoWClass, Dictionary<WoWItemStatType, float>> StatWeights = new Dictionary<WoWClass, Dictionary<WoWItemStatType, float>>();

        static AutoEquipper()
        {
            InitializeStatWeights();
        }

        /// <summary>
        /// Evaluates quest reward items and returns the best one for the character
        /// </summary>
        /// <param name="rewards">Array of available rewards</param>
        /// <returns>Best reward item or null if none suitable</returns>
        public QuestRewardItem EvaluateRewards(QuestRewardItem[] rewards)
        {
            if (rewards == null || rewards.Length == 0)
                return null;

            // Filter out null entries
            QuestRewardItem[] validRewards = rewards.Where(r => r != null && r.ItemInfo != null).ToArray();
            if (validRewards.Length == 0)
                return null;

            QuestRewardItem bestReward = null;
            float bestScore = float.MinValue;

            foreach (QuestRewardItem reward in validRewards)
            {
                float score = EvaluateItem(reward.ItemInfo, reward.Count);
                
                if (score > bestScore)
                {
                    bestScore = score;
                    bestReward = reward;
                }
            }

            // Only return the item if it scores better than vendor price baseline
            if (bestReward != null && bestScore > 0)
            {
                Logging.Write("[AutoEquip] Selected {0} (score: {1:F2})", bestReward.ItemInfo.Name, bestScore);
                return bestReward;
            }

            return null;
        }

        /// <summary>
        /// Evaluates an item based on character class and basic stats
        /// </summary>
        public bool IsUpgrade(ItemInfo item)
        {
            if (item == null || ObjectManager.Me?.Inventory == null)
                return false;

            WoWInventorySlot slot = GetItemSlot(item);
            if (slot == WoWInventorySlot.None)
                return false;

            // Keep the usability check: an unusable item can never be an upgrade.
            if (!ObjectManager.Me.CanEquipItem(item))
                return false;

            // An eligible item is always an upgrade when its target slot is free, regardless of score.
            if (IsSlotEmpty(slot) || IsSlotEmpty(GetSecondaryItemSlot(item)))
                return true;

            // Occupied slot: a positive evaluated score is still required.
            return EvaluateItem(item, 1) > 0;
        }

        private static bool IsSlotEmpty(WoWInventorySlot slot)
        {
            if (slot == WoWInventorySlot.None)
                return false;

            WoWPlayerInventory inventory = ObjectManager.Me?.Inventory;
            if (inventory == null)
                return false;

            WoWItem equipped = inventory.GetItemBySlot((uint)slot);
            return equipped == null || equipped.ItemInfo == null;
        }

        /// <summary>
        /// True when the candidate is a two-handed weapon that also removes the current off-hand item
        /// (Titan's Grip keeps the off-hand, so it is excluded).
        /// </summary>
        private static bool DisplacesOffHand(ItemInfo item)
        {
            if (item == null || item.InventoryType != InventoryType.TwoHandWeapon)
                return false;
            return !SpellManager.HasSpell("Titan's Grip");
        }

        /// <summary>
        /// True when the character can legally wield this one-handed weapon in the off-hand.
        /// </summary>
        private static bool CanDualWield(ItemInfo item)
        {
            if (item == null)
                return false;
            if (item.InventoryType != InventoryType.Weapon && item.InventoryType != InventoryType.WeaponOffHand)
                return false;
            return SpellManager.HasSpell("Dual Wield") || SpellManager.HasSpell("Titan's Grip");
        }

        private float EvaluateItem(ItemInfo item, int count, bool compareEquipped = true)
        {
            if (item == null)
                return 0f;

            // Check if we can equip it
            if (!ObjectManager.Me.CanEquipItem(item))
                return 0f;

            float score = 0f;
            if (StatWeights.TryGetValue(StyxWoW.Me.Class, out var weights))
                foreach (var stat in item.GetItemStats())
                    if (weights.TryGetValue((WoWItemStatType)(int)stat.Key, out float weight))
                        score += stat.Value * weight;

            // Simple evaluation: bonus for correct armor type
            if (item.ItemClass == WoWItemClass.Armor && item.Armor > 0)
            {
                WoWItemArmorClass wantedArmorClass = GetWantedArmorClass();
                if (item.ArmorClass == wantedArmorClass)
                {
                    // Heavily favor correct armor type
                    score += item.Armor * 0.5f;
                    score += 50f; // Flat bonus for correct type
                }
                else if (item.ArmorClass != WoWItemArmorClass.None)
                {
                    // Light penalty for wrong armor type
                    score += item.Armor * 0.1f;
                }
            }

            // Bonus for weapon DPS
            if (item.ItemClass == WoWItemClass.Weapon && item.DPS > 0)
            {
                score += item.DPS * 3.0f; // DPS is very valuable
            }

            // Bug #5: guard against null Inventory (ObjectManager.Wow == null during startup/shutdown)
            WoWPlayerInventory inventory = StyxWoW.Me.Inventory;
            if (inventory == null)
                return score;

            // Bug #3: for rings and trinkets, check BOTH slots and compare against the weaker one.
            // HB 6.2.3 yields Finger1+Finger2 and Trinket1+Trinket2 for dual-slot types.
            // Previously only Finger1/Trinket1 was checked, meaning Finger2/Trinket2 was never replaced.
            WoWInventorySlot slot1 = GetItemSlot(item);
            if (compareEquipped && slot1 != WoWInventorySlot.None)
            {
                WoWInventorySlot slot2 = GetSecondaryItemSlot(item);
                if (slot2 != WoWInventorySlot.None)
                {
                    // Dual-slot types (rings, trinkets, dual-wield weapons): compare against the weaker
                    // of the two slots so the weaker slot can still be replaced.
                    float weakestEquippedScore = float.MaxValue;
                    bool anySlotEmpty = false;
                    foreach (WoWInventorySlot checkSlot in new[] { slot1, slot2 })
                    {
                        WoWItem equipped = inventory.GetItemBySlot((uint)checkSlot);
                        if (equipped == null || equipped.ItemInfo == null)
                        {
                            // Empty slot — item is automatically an upgrade
                            anySlotEmpty = true;
                            break;
                        }
                        float equippedScore = EvaluateItem(equipped.ItemInfo, 1, false);
                        if (equippedScore < weakestEquippedScore)
                            weakestEquippedScore = equippedScore;
                    }

                    // Only consider it if it's at least 5% better than the weakest slot
                    if (!anySlotEmpty && score <= weakestEquippedScore * 1.05f)
                        return 0f;
                }
                else
                {
                    // Single-slot types: compare against the combined score of every item the equip
                    // action would remove (a two-hander also displaces the off-hand item).
                    float replacedScore = 0f;
                    bool anySlotEmpty = false;
                    foreach (WoWInventorySlot checkSlot in SlotsReplacedBy(item))
                    {
                        WoWItem equipped = inventory.GetItemBySlot((uint)checkSlot);
                        if (equipped == null || equipped.ItemInfo == null)
                        {
                            anySlotEmpty = true;
                            continue;
                        }
                        replacedScore += EvaluateItem(equipped.ItemInfo, 1, false);
                    }

                    if (!anySlotEmpty && score <= replacedScore * 1.05f)
                        return 0f;
                }
            }

            return score;
        }

        /// <summary>
        /// Every inventory slot the equip action would replace for this candidate.
        /// </summary>
        private IReadOnlyList<WoWInventorySlot> SlotsReplacedBy(ItemInfo item)
        {
            var slots = new List<WoWInventorySlot> { GetItemSlot(item) };
            if (DisplacesOffHand(item))
                slots.Add(WoWInventorySlot.OffHand);
            return slots;
        }

        /// <summary>
        /// Returns the secondary inventory slot for dual-slot item types (rings, trinkets, and
        /// one-handed weapons the character can dual-wield).
        /// Returns <see cref="WoWInventorySlot.None"/> for single-slot types.
        /// HB 6.2.3 pattern: yield Finger1 + Finger2, Trinket1 + Trinket2.
        /// </summary>
        private static WoWInventorySlot GetSecondaryItemSlot(ItemInfo item)
        {
            switch (item.InventoryType)
            {
                case InventoryType.Finger:  return WoWInventorySlot.Finger2;
                case InventoryType.Trinket: return WoWInventorySlot.Trinket2;
                case InventoryType.Weapon:
                case InventoryType.WeaponOffHand:
                    // A one-hander can also replace the off-hand when the class can dual-wield.
                    return CanDualWield(item) ? WoWInventorySlot.OffHand : WoWInventorySlot.None;
                default:                    return WoWInventorySlot.None;
            }
        }

        /// <summary>
        /// Gets the preferred armor class for the character
        /// </summary>
        private WoWItemArmorClass GetWantedArmorClass()
        {
            switch (StyxWoW.Me.Class)
            {
                case WoWClass.Warrior:
                case WoWClass.Paladin:
                case WoWClass.DeathKnight:
                    return StyxWoW.Me.Level >= 40 ? WoWItemArmorClass.Plate : WoWItemArmorClass.Mail;
                case WoWClass.Hunter:
                case WoWClass.Shaman:
                    return StyxWoW.Me.Level >= 40 ? WoWItemArmorClass.Mail : WoWItemArmorClass.Leather;
                case WoWClass.Rogue:
                case WoWClass.Druid:
                    return WoWItemArmorClass.Leather;
                case WoWClass.Priest:
                case WoWClass.Mage:
                case WoWClass.Warlock:
                    return WoWItemArmorClass.Cloth;
                default:
                    return WoWItemArmorClass.None;
            }
        }

        /// <summary>
        /// Gets the inventory slot for an item
        /// </summary>
        private WoWInventorySlot GetItemSlot(ItemInfo item)
        {
            switch (item.InventoryType)
            {
                case InventoryType.Head: return WoWInventorySlot.Head;
                case InventoryType.Neck: return WoWInventorySlot.Neck;
                case InventoryType.Shoulder: return WoWInventorySlot.Shoulders;
                case InventoryType.Body: return WoWInventorySlot.Shirt;
                case InventoryType.Chest:
                case InventoryType.Robe: return WoWInventorySlot.Chest;
                case InventoryType.Waist: return WoWInventorySlot.Waist;
                case InventoryType.Legs: return WoWInventorySlot.Legs;
                case InventoryType.Feet: return WoWInventorySlot.Feet;
                case InventoryType.Wrist: return WoWInventorySlot.Wrists;
                case InventoryType.Hand: return WoWInventorySlot.Hands;
                case InventoryType.Finger: return WoWInventorySlot.Finger1; // Check both finger slots later
                case InventoryType.Trinket: return WoWInventorySlot.Trinket1; // Check both trinket slots later
                case InventoryType.Cloak: return WoWInventorySlot.Back;
                case InventoryType.Weapon:
                case InventoryType.TwoHandWeapon:
                case InventoryType.WeaponMainHand: return WoWInventorySlot.MainHand;
                case InventoryType.WeaponOffHand:
                case InventoryType.Shield:
                case InventoryType.Holdable: return WoWInventorySlot.OffHand;
                case InventoryType.Ranged:
                case InventoryType.RangedRight:
                case InventoryType.Thrown:
                case InventoryType.Relic: return WoWInventorySlot.Ranged;
                case InventoryType.Tabard: return WoWInventorySlot.Tabard;
                default: return WoWInventorySlot.None;
            }
        }

        /// <summary>
        /// Initialize stat weights for each class based on WotLK priorities
        /// Simplified version - primarily for future expansion
        /// </summary>
        private static void InitializeStatWeights()
        {
            // Warrior - Physical DPS
            StatWeights[WoWClass.Warrior] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Strength, 2.0f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.CriticalStrikeRating, 1.5f },
                { WoWItemStatType.HitRating, 1.8f },
                { WoWItemStatType.ExpertiseRating, 1.6f },
                { WoWItemStatType.ArmorPenetration, 1.4f },
                { WoWItemStatType.Agility, 0.8f },
                { WoWItemStatType.Stamina, 0.5f }
            };

            // Paladin - Ret/Prot hybrid
            StatWeights[WoWClass.Paladin] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Strength, 2.0f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.CriticalStrikeRating, 1.3f },
                { WoWItemStatType.HitRating, 1.6f },
                { WoWItemStatType.ExpertiseRating, 1.4f },
                { WoWItemStatType.Stamina, 0.8f },
                { WoWItemStatType.Intellect, 0.5f },
                { WoWItemStatType.SpellPower, 0.7f }
            };

            // Hunter - Physical DPS
            StatWeights[WoWClass.Hunter] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Agility, 2.5f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.RangedPower, 1.1f },
                { WoWItemStatType.CriticalStrikeRating, 1.5f },
                { WoWItemStatType.HitRating, 1.8f },
                { WoWItemStatType.ArmorPenetration, 1.3f },
                { WoWItemStatType.Stamina, 0.5f }
            };

            // Rogue - Physical DPS
            StatWeights[WoWClass.Rogue] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Agility, 2.5f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.CriticalStrikeRating, 1.6f },
                { WoWItemStatType.HitRating, 1.9f },
                { WoWItemStatType.ExpertiseRating, 1.7f },
                { WoWItemStatType.ArmorPenetration, 1.4f },
                { WoWItemStatType.Stamina, 0.4f }
            };

            // Priest - Spell caster
            StatWeights[WoWClass.Priest] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.SpellPower, 2.0f },
                { WoWItemStatType.Intellect, 1.5f },
                { WoWItemStatType.Spirit, 1.2f },
                { WoWItemStatType.HitRating, 1.6f },
                { WoWItemStatType.CriticalStrikeRating, 1.3f },
                { WoWItemStatType.HasteRating, 1.2f },
                { WoWItemStatType.Stamina, 0.5f }
            };

            // Shaman - Hybrid
            StatWeights[WoWClass.Shaman] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.SpellPower, 1.8f },
                { WoWItemStatType.Intellect, 1.4f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.Agility, 1.2f },
                { WoWItemStatType.HitRating, 1.5f },
                { WoWItemStatType.CriticalStrikeRating, 1.3f },
                { WoWItemStatType.HasteRating, 1.1f },
                { WoWItemStatType.Stamina, 0.6f }
            };

            // Mage - Spell DPS
            StatWeights[WoWClass.Mage] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.SpellPower, 2.2f },
                { WoWItemStatType.Intellect, 1.6f },
                { WoWItemStatType.HitRating, 1.8f },
                { WoWItemStatType.CriticalStrikeRating, 1.4f },
                { WoWItemStatType.HasteRating, 1.3f },
                { WoWItemStatType.Spirit, 0.8f },
                { WoWItemStatType.Stamina, 0.4f }
            };

            // Warlock - Spell DPS
            StatWeights[WoWClass.Warlock] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.SpellPower, 2.0f },
                { WoWItemStatType.Intellect, 1.5f },
                { WoWItemStatType.HitRating, 1.8f },
                { WoWItemStatType.CriticalStrikeRating, 1.4f },
                { WoWItemStatType.HasteRating, 1.3f },
                { WoWItemStatType.Spirit, 1.0f },
                { WoWItemStatType.Stamina, 0.5f }
            };

            // Druid - Feral/Balance hybrid
            StatWeights[WoWClass.Druid] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Agility, 2.0f },
                { WoWItemStatType.Strength, 1.5f },
                { WoWItemStatType.SpellPower, 1.6f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.Intellect, 1.2f },
                { WoWItemStatType.CriticalStrikeRating, 1.3f },
                { WoWItemStatType.HitRating, 1.5f },
                { WoWItemStatType.Stamina, 0.7f }
            };

            // Death Knight - DPS/Tank hybrid
            StatWeights[WoWClass.DeathKnight] = new Dictionary<WoWItemStatType, float>
            {
                { WoWItemStatType.Strength, 2.2f },
                { WoWItemStatType.AttackPower, 1.0f },
                { WoWItemStatType.CriticalStrikeRating, 1.4f },
                { WoWItemStatType.HitRating, 1.7f },
                { WoWItemStatType.ExpertiseRating, 1.5f },
                { WoWItemStatType.ArmorPenetration, 1.3f },
                { WoWItemStatType.Stamina, 0.8f }
            };
        }
    }
}
