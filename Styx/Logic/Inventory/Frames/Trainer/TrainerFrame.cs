using Styx.WoWInternals;

namespace Styx.Logic.Inventory.Frames.Trainer
{
	/// <summary>
	/// Represents the class trainer frame in WoW.
	/// </summary>
	public class TrainerFrame : Frame
	{
		public static readonly TrainerFrame Instance = new TrainerFrame();

		public TrainerFrame() : base("ClassTrainerFrame")
		{
		}

		/// <summary>
		/// Gets whether the trainer frame is visible.
		/// </summary>
		public new bool IsVisible => Lua.GetReturnVal<bool>("return ClassTrainerFrame and ClassTrainerFrame:IsVisible()", 0);

		/// <summary>
		/// Gets the number of trainer services available.
		/// </summary>
		public int NumTrainerServices => Lua.GetReturnVal<int>("return GetNumTrainerServices()", 0);

		/// <summary>
		/// Returns true when at least one trainer row is still marked available (trainable).
		/// Rows are inspected by their reported category, not by their display position.
		/// </summary>
		public bool TryHasPurchasableService(out bool hasPurchasableService)
		{
			// Expand collapsed headers so their child services are counted. We deliberately
			// leave them expanded: this is a stable view for later checks and avoids
			// collapsing rows the player may be interacting with. Expand from the end so
			// earlier indices remain valid as rows are inserted.
			const string script =
				"local count = GetNumTrainerServices and GetNumTrainerServices() or nil; " +
				"if not count then return 'unreadable' end; " +
				"local headers = {}; " +
				"for i = 1, count do " +
				"  local _, _, category, expanded = GetTrainerServiceInfo(i); " +
				"  if category == 'header' and not expanded then headers[#headers + 1] = i end " +
				"end; " +
				"for j = #headers, 1, -1 do " +
				"  if ExpandTrainerSkillLine then ExpandTrainerSkillLine(headers[j]) end " +
				"end; " +
				"count = GetNumTrainerServices(); " +
				"for i = 1, count do " +
				"  local _, _, category = GetTrainerServiceInfo(i); " +
				"  if category == 'available' then return '1' end " +
				"end " +
				"return '0'";
			string raw = Lua.GetReturnVal<string>(script, 0U);
			if (raw == "1")
			{
				hasPurchasableService = true;
				return true;
			}
			if (raw == "0")
			{
				hasPurchasableService = false;
				return true;
			}
			hasPurchasableService = false;
			return false;
		}

		/// <summary>
		/// True when a service remains available, or when the trainer list could not be
		/// read (failures must not be reported as completed training).
		/// </summary>
		public bool HasPurchasableService() =>
			!TryHasPurchasableService(out bool hasPurchasableService) || hasPurchasableService;

		/// <summary>
		/// Gets whether this is a tradeskill trainer.
		/// </summary>
		public bool IsTradeskillTrainer => Lua.GetReturnVal<bool>("return IsTradeskillTrainer()", 0);

		/// <summary>
		/// Gets the currently selected trainer service index.
		/// </summary>
		public int Selected => Lua.GetReturnVal<int>("return GetTrainerSelectionIndex()", 0);

		/// <summary>
		/// Sets a trainer service filter.
		/// </summary>
		public void SetServiceFilter(TrainerServiceFilter filter, bool show)
		{
			Lua.DoString($"SetTrainerServiceTypeFilter('{filter.ToString().ToLower()}',{(show ? 1 : 0)})");
		}

		/// <summary>
		/// Gets the cost of a trainer service.
		/// </summary>
		public int GetServiceCost(int index)
		{
			return Lua.GetReturnVal<int>($"return GetTrainerServiceCost({index + 1})", 0);
		}

		/// <summary>
		/// Closes the trainer frame.
		/// </summary>
		public void Close()
		{
			Lua.DoString("CloseTrainer()");
		}

		/// <summary>
		/// Buys a specific trainer service by index.
		/// </summary>
		public void Buy(int index)
		{
			Lua.DoString($"BuyTrainerService({index + 1})");
		}

		/// <summary>
		/// Buys all available trainer services.
		/// </summary>
		public bool BuyAll()
		{
			return Lua.GetReturnVal<bool>("BuyTrainerService(0); return GetNumTrainerServices();", 0);
		}
	}
}
