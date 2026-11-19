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

            return PutItem(luaBag, luaSlot, tradeSlot, splitCount, (int)item.StackCount, item.Entry);
        }

        /// <summary>
        /// Places an item from Lua bag (0-4) and slot (1-based) into trade slot (1-6).
        /// </summary>
        public bool PutItem(int luaBagIndex, int luaBagSlot, int tradeSlot, int splitCount = 0, int currentStackCount = 0, uint expectedEntry = 0)
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

            bool cursorEmpty = Lua.GetReturnVal<int>("if CursorHasItem() then return 1 else return 0 end", 0U) != 1;
            int expectedCount = splitCount > 0 ? splitCount : currentStackCount;
            // An empty cursor alone is not success: verify the intended item landed in
            // the intended trade slot with the expected quantity.
            bool slotVerified = expectedEntry == 0 || expectedCount <= 0 ||
                ContainsPlayerItem(tradeSlot, expectedEntry, expectedCount);
            if (!cursorEmpty || !slotVerified)
            {
                if (!cursorEmpty)
                    Lua.DoString("ClearCursor()");
                return false;
            }

            return true;
        }

        public bool ContainsPlayerItem(int slot, uint entry, int count) => Lua.GetReturnVal<int>(
            $"local link=GetTradePlayerItemLink({slot}); local _,_,n=GetTradePlayerItemInfo({slot}); if link and tonumber(string.match(link,'item:(%d+)')) == {entry} and n == {count} then return 1 end return 0", 0U) == 1;

        public bool HasEmptyPlayerOffer => Lua.GetReturnVal<int>(
            "if GetPlayerTradeMoney() ~= 0 then return 0 end for i=1,7 do if GetTradePlayerItemLink(i) then return 0 end end return 1", 0U) == 1;

        // Returns -1 when the value cannot be read (including when the frame is gone),
        // so callers never mistake a failed read for a valid offer of zero copper.
        public int PlayerTradeMoney
        {
            get
            {
                string raw = Lua.GetReturnVal<string>(
                    "if TradeFrame and TradeFrame:IsVisible() then local m = GetPlayerTradeMoney() if m ~= nil then return tostring(m) end end return 'unreadable'",
                    0U);
                return int.TryParse(raw, out int money) ? money : -1;
            }
        }

        /// <summary>
        /// Compact signature of the local offer: slot:entry:count for trade slots 1-6.
        /// Empty slots have entry 0 and count 0.
        /// </summary>
        public string PlayerOfferSignature => Lua.GetReturnVal<string>(
            "local p={} for i=1,6 do local link=GetTradePlayerItemLink(i); local _,_,n=GetTradePlayerItemInfo(i); " +
            "local e=0; if link then e=tonumber(string.match(link,'item:(%d+)')) or 0 end " +
            "p[#p+1]=i..':'..e..':'..(n or 0) end return table.concat(p,';')", 0U) ?? string.Empty;

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
