using System;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Inventory.Frames.Trade
{
    /// <summary>
    /// Wrapper around the WoW 3.3.5a player-to-player TradeFrame.
    /// </summary>
    public class TradeFrame : Frame
    {
        public static readonly TradeFrame Instance = new TradeFrame();

        public TradeFrame()
            : base("TradeFrame")
        {
        }

        public new bool IsVisible =>
            Lua.GetReturnVal<int>("if TradeFrame and TradeFrame:IsVisible() then return 1 else return 0 end", 0U) == 1;

        public string PartnerName =>
            Lua.GetReturnVal<string>(
                "return UnitName('NPC') or (TradeFrameRecipientNameText and TradeFrameRecipientNameText:GetText()) or ''",
                0U) ?? string.Empty;

        public bool PlayerAccepted =>
            Lua.GetReturnVal<int>(
                "if TradeFrame and TradeFrame.acceptState == 1 then return 1 else return 0 end",
                0U) == 1;

        public bool TargetAccepted =>
            Lua.GetReturnVal<int>(
                "if TradeHighlightRecipient and TradeHighlightRecipient:IsVisible() then return 1 else return 0 end",
                0U) == 1;

        /// <summary>
        /// Validates that the trade partner matches the coordinator-authorized provisioner name.
        /// </summary>
        public static bool IsPartnerAuthorized(string? partnerName, string? expectedPartnerName)
        {
            if (string.IsNullOrWhiteSpace(partnerName) || string.IsNullOrWhiteSpace(expectedPartnerName))
                return false;

            return string.Equals(partnerName.Trim(), expectedPartnerName.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Initiates a trade request with the named unit if within range.
        /// </summary>
        public void InitiateTrade(string unitToken)
        {
            string escaped = Lua.Escape(unitToken ?? "target");
            Lua.DoString($"InitiateTrade('{escaped}')");
        }

        /// <summary>
        /// Places an item (or split stack) from a WoWItem into trade slot 1..6.
        /// Uses WoWItem.BagIndex + 1 (so Backpack -1 -> Lua bag 0) and BagSlot + 1 (1-based).
        /// </summary>
        public bool PutItem(WoWItem item, int tradeSlot, int splitCount = 0)
        {
            if (item == null || !item.IsValid)
                return false;

            int luaBag = item.BagIndex + 1;
            int luaSlot = item.BagSlot + 1;
            if (luaBag < 0 || luaSlot <= 0)
                return false;

            return PutItem(luaBag, luaSlot, tradeSlot, splitCount, (int)item.StackCount);
        }

        /// <summary>
        /// Places an item from Lua bag (0-4) and slot (1-based) into trade slot (1-6).
        /// </summary>
        public bool PutItem(int luaBagIndex, int luaBagSlot, int tradeSlot, int splitCount = 0, int currentStackCount = 0)
        {
            if (!IsVisible || tradeSlot < 1 || tradeSlot > 6 ||
                Lua.GetReturnVal<int>("if CursorHasItem() then return 1 else return 0 end", 0U) == 1)
                return false;

            if (splitCount > 0 && (currentStackCount == 0 || splitCount < currentStackCount))
            {
                Lua.DoString($"SplitContainerItem({luaBagIndex}, {luaBagSlot}, {splitCount}) ClickTradeButton({tradeSlot})");
            }
            else
            {
                Lua.DoString($"PickupContainerItem({luaBagIndex}, {luaBagSlot}) ClickTradeButton({tradeSlot})");
            }

            if (Lua.GetReturnVal<int>("if CursorHasItem() then return 1 else return 0 end", 0U) == 1)
            {
                Lua.DoString("ClearCursor()");
                return false;
            }

            return true;
        }

        public bool ContainsPlayerItem(int slot, uint entry, int count) => Lua.GetReturnVal<int>(
            $"local link=GetTradePlayerItemLink({slot}); local _,_,n=GetTradePlayerItemInfo({slot}); if link and tonumber(string.match(link,'item:(%d+)')) == {entry} and n == {count} then return 1 end return 0", 0U) == 1;

        /// <summary>
        /// True when the player's side of the open trade window contains no gold.
        /// </summary>
        public bool HasEmptyPlayerMoney => Lua.GetReturnVal<int>(
            "if (GetPlayerTradeMoney() or 0) == 0 then return 1 end return 0", 0U) == 1;

        /// <summary>
        /// Number of item slots the player has filled in the open trade window (0-6).
        /// Evaluated in a single Lua chunk to avoid one round-trip per slot.
        /// </summary>
        public int PlayerOfferedItemCount => Lua.GetReturnVal<int>(
            "local n=0 for i=1,6 do if GetTradePlayerItemLink(i) then n=n+1 end end return n", 0U);

        public bool HasEmptyPlayerOffer => Lua.GetReturnVal<int>(
            "if GetPlayerTradeMoney() ~= 0 then return 0 end for i=1,7 do if GetTradePlayerItemLink(i) then return 0 end end return 1", 0U) == 1;

        public void AcceptTrade()
        {
            Lua.DoString("AcceptTrade()");
        }

        public void CancelTrade()
        {
            Lua.DoString("CancelTrade()");
        }
    }
}
