using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Styx.Helpers;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;

namespace Styx.Logic.Inventory.Frames.MailBox
{
	/// <summary>
	/// Represents the mail frame in WoW.
	/// </summary>
	public class MailFrame : Frame
	{
		public static readonly MailFrame Instance = new MailFrame();

		// How many consecutive failed loot attempts are tolerated for the same mail
		// before OpenAllMail gives up and reports the batch as incomplete.
		private const int MaxLootAttemptsPerMail = 3;

		public MailFrame() : base("MailFrame")
		{
		}

		/// <summary>
		/// Gets the number of mail items in the inbox.
		/// </summary>
		public int MailCount
		{
			get
			{
				// Public API keeps its "0 when unavailable" fallback.
				return TryGetMailCount(out int count) ? count : 0;
			}
		}

		/// <summary>
		/// Reads the inbox item count and reports whether the read produced a usable
		/// number.  Callers that compare counts (loot confirmation, batch sizing) must
		/// use this instead of <see cref="MailCount"/>, otherwise the public 0 fallback
		/// looks like "all mail gone".
		/// </summary>
		private static bool TryGetMailCount(out int count)
		{
			count = 0;
			var result = Lua.GetReturnValues("return GetInboxNumItems()", "Economist.lua");
			if (result == null || result.Count <= 0 || string.IsNullOrEmpty(result[0]))
				return false;

			return int.TryParse(result[0], out count);
		}

		/// <summary>
		/// Gets whether there is new mail.
		/// </summary>
		public bool HasNewMail
		{
			get
			{
				Lua.DoString("CheckInbox()");
				var result = Lua.GetReturnValues("return HasNewMail()", "Economist.lua");
				if (result == null || result.Count <= 0 || string.IsNullOrEmpty(result[0]))
					return false;
				return int.TryParse(result[0], out int hasNewMail) && hasNewMail != 0;
			}
		}

		/// <summary>
		/// Deletes a mail item.
		/// </summary>
		public void DeleteMail(int mailIndex)
		{
			Lua.DoString($"DeleteInboxItem({mailIndex})", "Economist.lua");
		}

		/// <summary>
		/// Returns a mail item to sender.
		/// </summary>
		public void ReturnMail(int mailIndex)
		{
			Lua.DoString($"ReturnInboxItem({mailIndex})", "Economist.lua");
		}

		/// <summary>
		/// Gets all attachments from a mail item.
		/// </summary>
		public bool GetMailAttachments(int mailIndex)
		{
			// Baseline must come from a *successful* count read: the public MailCount
			// fallback of 0 would make the confirmation compare meaningless.
			bool haveBaseline = TryGetMailCount(out int mailCount);
			int currentMoney = GetMoney();

			var headerResult = Lua.GetReturnValues($"return GetInboxHeaderInfo({mailIndex})", "Economist.lua");
			if (headerResult == null || headerResult.Count < 8)
			{
				Logging.WriteDebug("GetMailAttachments: no header for mail {0}; nothing collected.", mailIndex);
				return false;
			}

			// headerResult[4] is nil for text-only mail, and GetMoney() can return nil
			// while the mailbox is closing — both used to throw from the wait loops.
			int mailMoney = 0;
			if (!string.IsNullOrEmpty(headerResult[4]))
				int.TryParse(headerResult[4], out mailMoney);

			Lua.DoString($"AutoLootMailItem({mailIndex})", "Economist.lua");

			// Bounded waits: a mail carrying no money (or a full gold cap that blocks the
			// delta) never satisfies these conditions, so unbounded loops hung the bot.
			int startTick = Environment.TickCount;
			if (mailMoney > 0)
			{
				while (Environment.TickCount - startTick < 5000 && GetMoney() < currentMoney + mailMoney)
					StyxWoW.Sleep(250);
			}

			if (!haveBaseline)
			{
				// Cannot verify the loot without a trustworthy baseline. Report failure so
				// the caller retries rather than advancing its batch on a bogus 0.
				Logging.WriteDebug("GetMailAttachments: could not read the mail count; collection of mail {0} unverified.", mailIndex);
				return false;
			}

			startTick = Environment.TickCount;
			while (Environment.TickCount - startTick < 5000)
			{
				// Only a successful read may confirm the loot.
				if (TryGetMailCount(out int remaining) && remaining < mailCount)
					return true;

				StyxWoW.Sleep(250);
			}

			Logging.WriteDebug("GetMailAttachments: timed out waiting for mail {0} to be looted.", mailIndex);
			return false;
		}

		/// <summary>
		/// Reads the player's copper amount, returning 0 when the Lua call yields no
		/// usable number (mailbox closed, player not in world, ...).
		/// </summary>
		private static int GetMoney()
		{
			var moneyResult = Lua.GetReturnValues("return GetMoney()", "Economist.lua");
			if (moneyResult == null || moneyResult.Count <= 0 || string.IsNullOrEmpty(moneyResult[0]))
				return 0;

			return int.TryParse(moneyResult[0], out int money) ? money : 0;
		}

		/// <summary>
		/// Opens all mail and collects attachments.
		/// </summary>
		public void OpenAllMail()
		{
			if (!IsVisible)
				return;

			// Looting always targets index 1: every collected mail shifts the rest down.
			// A failed attempt must not consume one of the "count" iterations, so retries
			// are bounded separately and a failure is reported instead of silently
			// leaving mail uncollected.
			// Batch sizing must come from a *successful* count read: the public MailCount
			// 0 fallback would look like "inbox empty" and exit the batch without
			// collecting (or reporting) anything.
			int remaining;
			if (!TryGetMailCount(out remaining))
			{
				Logging.WriteDebug("OpenAllMail: inbox count unavailable; skipping this batch.");
				return;
			}

			int consecutiveFailures = 0;

			while (remaining > 0 && consecutiveFailures < MaxLootAttemptsPerMail)
			{
				if (GetMailAttachments(1))
				{
					remaining--;
					consecutiveFailures = 0;
					continue;
				}

				consecutiveFailures++;
				StyxWoW.Sleep(250);
			}

			if (remaining > 0)
				Logging.WriteDebug("OpenAllMail: incomplete — {0} mail(s) could not be collected.", remaining);
		}

		/// <summary>
		/// Opens all mail from a specific sender.
		/// </summary>
		public void OpenAllMailFrom(string senderName)
		{
			if (IsVisible)
			{
				Lua.DoString("CheckInbox()");

				// Same reasoning as OpenAllMail: a failed read must not silently select no
				// mail at all.  MailCount's 0 fallback is kept for API compatibility only.
				int count;
				if (!TryGetMailCount(out count))
				{
					Logging.WriteDebug("OpenAllMailFrom: inbox count unavailable; skipping this batch.");
					return;
				}

				var mailIndicesToCollect = new List<int>(count);

				for (int i = 1; i <= count; i++)
				{
					var headerResult = Lua.GetReturnValues($"return GetInboxHeaderInfo({i})", "Economist.lua");
					if (headerResult != null && headerResult.Count >= 8)
					{
						string sender = headerResult[2];
						if (!string.IsNullOrEmpty(sender) && !string.IsNullOrEmpty(senderName) &&
						    sender.ToLower().Contains(senderName.ToLower()))
							mailIndicesToCollect.Add(i);
					}
				}

				mailIndicesToCollect.Sort((a, b) => b.CompareTo(a));

				foreach (int idx in mailIndicesToCollect)
					GetMailAttachments(idx);
			}
		}

		/// <summary>
		/// Switches to the send mail tab.
		/// </summary>
		public void SwitchToSendMailTab()
		{
			Lua.DoString("MailFrameTab2:Click()");
		}

		/// <summary>
		/// Sends a mail with attachments.
		/// </summary>
		public void SendMail(string recipient, string subject, string body, int copper, params WoWItem[] attachmentItems)
		{
			if (!IsVisible)
				return;

			if (attachmentItems.Length > 12)
				throw new ArgumentException("You can not attach more than 12 items!", nameof(attachmentItems));

			Lua.DoString("ClearSendMail()");
			SwitchToSendMailTab();
			// Escape before interpolating: a recipient/subject/body containing a quote
			// (or a newline from a profile) otherwise produced a syntactically invalid
			// Lua chunk, so the mail was never sent — or worse, executed as code.
			string escapedRecipient = Lua.Escape(recipient ?? string.Empty);
			string escapedSubject = Lua.Escape(subject ?? string.Empty);
			string escapedBody = Lua.Escape(body ?? string.Empty);
			Lua.DoString($"SendMailNameEditBox:SetText(\"{escapedRecipient}\")", "_main.lua");
			Lua.DoString($"SetSendMailMoney({copper})", "_main.lua");

			if (attachmentItems.Length > 0)
			{
				for (int i = 0; i < attachmentItems.Length; i++)
				{
					attachmentItems[i].UseContainerItem();
					StyxWoW.Sleep(100);
				}

				if (SendMailItems.Length != attachmentItems.Length)
					Logging.Write("Something is messed up, we didn't attach all of our items.");
			}

			Lua.DoString($"SendMail(\"{escapedRecipient}\", \"{escapedSubject}\", \"{escapedBody}\")", "_main.lua");
		}

		/// <summary>
		/// Sends a mail with many attachments (splits into multiple mails if needed).
		/// </summary>
		public void SendMailWithManyAttachments(string recipient, int copper, params WoWItem[] attachments)
		{
			int numMails = (int)Math.Ceiling((float)attachments.Length / 12f);
			var itemList = attachments.ToList();

			for (int i = 0; i < numMails; i++)
			{
				var batch = itemList.Take(12).ToList();
				if (batch.Count > 0)
					itemList.RemoveRange(0, batch.Count);

				// Snapshot item count BEFORE send, with fresh memory read.
				// HB 3.3.5a: clear cache so we read the actual in-game bag state,
				// not the tick-cached snapshot from before entering the mail behavior.
				StyxWoW.Memory.ClearCache();
				var currentItems = ObjectManager.GetObjectsOfType<WoWItem>();
				int count = currentItems.Count;

				SendMail(recipient, batch.Count > 0 ? batch[0].Name : "items", "", copper, batch.ToArray());

				// Wait for WoW to remove the mailed items from the bag (server acknowledgment).
				// Must clear cache each iteration so we see the updated bag state.
				int startTick = Environment.TickCount;
				while (Environment.TickCount - startTick < 10000)
				{
					StyxWoW.Sleep(250);
					StyxWoW.Memory.ClearCache();
					currentItems = ObjectManager.GetObjectsOfType<WoWItem>();
					if (currentItems.Count < count)
						break; // items removed — mail confirmed
				}
			}
		}

		/// <summary>
		/// Hides the mail frame.
		/// </summary>
		public new void Hide()
		{
			Lua.DoString("CloseMail()");
		}

		/// <summary>
		/// Closes the mail frame.
		/// </summary>
		public void Close() => Hide();

		/// <summary>
		/// FEAT-33: Gets the GUIDs of items currently attached to the outgoing mail.
		/// Uses Lua to check ITEM_QUALITY_COLORS-based attachment slots (max 12 slots in WotLK).
		/// </summary>
		public ulong[] SendMailItemGuids
		{
			get
			{
				var guids = new List<ulong>();
				try
				{
					for (int i = 1; i <= 12; i++)
					{
						var results = Lua.GetReturnValues(
							$"local name, itemId, texture, count, quality = GetSendMailItem({i}); return itemId or 0");
						if (results != null && results.Count > 0)
						{
							int itemId = Lua.ParseLuaValue<int>(results[0]);
							if (itemId > 0)
							{
								// Find first matching item in bags with this entry
								foreach (var item in ObjectManager.GetObjectsOfType<WoWItem>())
								{
									if (item.Entry == (uint)itemId && item.IsValid)
									{
										guids.Add(item.Guid);
										break;
									}
								}
							}
						}
					}
				}
				catch { }
				return guids.ToArray();
			}
		}

		/// <summary>
		/// FEAT-33: Gets the WoWItem objects attached to the outgoing mail.
		/// </summary>
		public WoWItem[] SendMailItems
		{
			get
			{
				var items = new List<WoWItem>();
				foreach (ulong guid in SendMailItemGuids)
				{
					var item = ObjectManager.GetObjectByGuid<WoWItem>(guid);
					if (item != null)
						items.Add(item);
				}
				return items.ToArray();
			}
		}
	}
}
