using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Styx.Logic.Profiles;
using Styx.PartyGrind;

using WpfClipboard = System.Windows.Clipboard;
using WpfMessageBox = System.Windows.MessageBox;

namespace CopilotBuddy.Bots.PartyGrind.Forms
{
    public partial class PartyGrindConfigWindow : Window
    {
        private bool _isLoading;

        public PartyGrindConfigWindow()
        {
            InitializeComponent();
            PopulateComboBoxes();
            LoadFromSettings(SquadSettings.Instance);
            GenerateScriptPreview();
        }

        private void PopulateComboBoxes()
        {
            _isLoading = true;
            CmbRole.ItemsSource = Enum.GetValues(typeof(SquadRole));
            CmbLevelAdvanceMode.ItemsSource = Enum.GetValues(typeof(SquadLevelAdvanceMode));
            CmbMountPolicy.ItemsSource = Enum.GetValues(typeof(SquadMountPolicy));
            CmbLootMethod.ItemsSource = Enum.GetValues(typeof(SquadLootMethod));
            CmbRollRule.ItemsSource = Enum.GetValues(typeof(SquadRollRule));
            CmbClientCount.ItemsSource = new[] { 3, 4, 5 };
            CmbClientCount.SelectedItem = 3;
            _isLoading = false;
        }

        private void LoadFromSettings(SquadSettings s)
        {
            _isLoading = true;
            try
            {
                TxtTeamId.Text = s.TeamId;
                TxtIpcPort.Text = s.IpcPort.ToString(CultureInfo.InvariantCulture);
                CmbRole.SelectedItem = s.Role;
                ChkIsCoordinator.IsChecked = s.IsCoordinator;
                TxtCoordinatorName.Text = s.CoordinatorName;
                TxtExpectedRoster.Text = s.ExpectedMemberNamesCsv;

                ChkAutoInvite.IsChecked = s.AutoInviteMembers;
                TxtInviteInterval.Text = s.InviteIntervalSeconds.ToString(CultureInfo.InvariantCulture);
                ChkAutoAcceptInvite.IsChecked = s.AutoAcceptCoordinatorInvite;
                ChkStrictWhitelist.IsChecked = s.StrictInviteWhitelist;
                ChkAutoLoadProfile.IsChecked = s.AutoLoadCoordinatorProfile;
                CmbLevelAdvanceMode.SelectedItem = s.LevelAdvanceMode;
                TxtDisconnectGrace.Text = s.DisconnectGraceSeconds.ToString(CultureInfo.InvariantCulture);

                TxtDpsDelay.Text = s.DpsEngageDelayMs.ToString(CultureInfo.InvariantCulture);
                ChkHealerAssistDps.IsChecked = s.HealerAssistDps;
                TxtHealerAssistMana.Text = s.HealerDpsAboveManaPct.ToString(CultureInfo.InvariantCulture);
                ChkEmergencySelfHeal.IsChecked = s.UseEmergencySelfHealBeforeParty;
                ChkForceTankAura.IsChecked = s.ForceTankDefensiveStanceOrAura;

                TxtSoftWait.Text = s.SoftWaitDistance.ToString(CultureInfo.InvariantCulture);
                TxtResumeDist.Text = s.ResumeDistance.ToString(CultureInfo.InvariantCulture);
                TxtHardBacktrack.Text = s.HardBacktrackDistance.ToString(CultureInfo.InvariantCulture);
                TxtMaxPullGather.Text = s.MaxPullGatherDistance.ToString(CultureInfo.InvariantCulture);
                TxtRangedStandoff.Text = s.RangedStandoffDistance.ToString(CultureInfo.InvariantCulture);
                TxtHealerStandoff.Text = s.HealerStandoffDistance.ToString(CultureInfo.InvariantCulture);
                TxtMeleeFlank.Text = s.MeleeFlankRadius.ToString(CultureInfo.InvariantCulture);
                CmbMountPolicy.SelectedItem = s.MountPolicy;

                TxtRestTriggerHp.Text = s.TriggerRestHealthPct.ToString(CultureInfo.InvariantCulture);
                TxtRestTriggerMp.Text = s.TriggerRestManaPct.ToString(CultureInfo.InvariantCulture);
                TxtRestResumeHp.Text = s.ResumeRestHealthPct.ToString(CultureInfo.InvariantCulture);
                TxtRestResumeMp.Text = s.ResumeRestManaPct.ToString(CultureInfo.InvariantCulture);

                CmbLootMethod.SelectedItem = s.LootMethod;
                CmbRollRule.SelectedItem = s.RollRule;
                TxtMaxLootSec.Text = s.MaxLootWindowSeconds.ToString(CultureInfo.InvariantCulture);
                ChkSyncTownRuns.IsChecked = s.SyncTownRuns;
                TxtTownBagSlots.Text = s.TriggerTownFreeBagSlots.ToString(CultureInfo.InvariantCulture);
                TxtTownDurability.Text = s.TriggerTownDurabilityPct.ToString(CultureInfo.InvariantCulture);
                ChkTrainSkills.IsChecked = s.TrainSkillsInGroupTownRun;
                TxtWaitRessSec.Text = s.WaitForPartyRessSeconds.ToString(CultureInfo.InvariantCulture);

                ChkEnableMageProv.IsChecked = s.EnableMageProvisioning;
                ChkPreferMageFood.IsChecked = s.PreferMageFoodOverVendor;
                TxtMinFood.Text = s.MinMemberFoodStack.ToString(CultureInfo.InvariantCulture);
                TxtTargetFood.Text = s.TargetMemberFoodStack.ToString(CultureInfo.InvariantCulture);
                TxtMinDrink.Text = s.MinMemberDrinkStack.ToString(CultureInfo.InvariantCulture);
                TxtTargetDrink.Text = s.TargetMemberDrinkStack.ToString(CultureInfo.InvariantCulture);
                TxtTradeTimeout.Text = s.TradeOfferTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
                TxtProvisionCooldown.Text = s.ProvisionCooldownSeconds.ToString(CultureInfo.InvariantCulture);
                ChkEnableWarlockHs.IsChecked = s.EnableWarlockHealthstones;
                ChkUseRitualTables.IsChecked = s.UseRitualTablesWhenAvailable;
                ChkEnableCrossBuffs.IsChecked = s.EnableCrossPartyBuffs;
                ChkRaidMarkers.IsChecked = s.SetRaidTargetMarkers;

                TxtScriptProfilePath.Text = string.IsNullOrWhiteSpace(ProfileManager.XmlLocation)
                    ? @"C:\Profiles\Party_1_80.xml"
                    : ProfileManager.XmlLocation;

                string[] existing = s.GetExpectedMemberNames();
                TxtScriptNames.Text = existing.Length >= 3
                    ? string.Join(",", existing)
                    : "TankNick,HealNick,DpsNick1";
                int count = Math.Clamp(existing.Length, 3, 5);
                CmbClientCount.SelectedItem = count;
                TxtScriptRoles.Text = string.Join(",", new[] { "TankLeader", "Healer", "DpsMelee", "DpsRanged", "HybridOffHeal" }.Take(count));
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void SaveToSettings(SquadSettings s)
        {
            float resume = ParseFloat(TxtResumeDist.Text, s.ResumeDistance, 5f, 100f);
            float soft = ParseFloat(TxtSoftWait.Text, s.SoftWaitDistance, 10f, 150f);
            float hard = ParseFloat(TxtHardBacktrack.Text, s.HardBacktrackDistance, 20f, 250f);
            if (!(resume < soft && soft < hard))
                throw new ArgumentException("Require Resume Distance < Soft Wait Distance < Hard Backtrack Distance.");
            s.TeamId = string.IsNullOrWhiteSpace(TxtTeamId.Text) ? "Squad1" : TxtTeamId.Text.Trim();
            s.IpcPort = ParseInt(TxtIpcPort.Text, s.IpcPort, 1024, 65535);
            if (CmbRole.SelectedItem is SquadRole role)
                s.Role = role;
            s.IsCoordinator = ChkIsCoordinator.IsChecked == true;
            s.CoordinatorName = TxtCoordinatorName.Text?.Trim() ?? string.Empty;
            s.ExpectedMemberNamesCsv = TxtExpectedRoster.Text?.Trim() ?? string.Empty;

            s.AutoInviteMembers = ChkAutoInvite.IsChecked == true;
            s.InviteIntervalSeconds = ParseInt(TxtInviteInterval.Text, s.InviteIntervalSeconds, 3, 300);
            s.AutoAcceptCoordinatorInvite = ChkAutoAcceptInvite.IsChecked == true;
            s.StrictInviteWhitelist = ChkStrictWhitelist.IsChecked == true;
            s.AutoLoadCoordinatorProfile = ChkAutoLoadProfile.IsChecked == true;
            if (CmbLevelAdvanceMode.SelectedItem is SquadLevelAdvanceMode mode)
                s.LevelAdvanceMode = mode;
            s.DisconnectGraceSeconds = ParseInt(TxtDisconnectGrace.Text, s.DisconnectGraceSeconds, 0, 3600);

            s.DpsEngageDelayMs = ParseInt(TxtDpsDelay.Text, s.DpsEngageDelayMs, 0, 10000);
            s.HealerAssistDps = ChkHealerAssistDps.IsChecked == true;
            s.HealerDpsAboveManaPct = ParseInt(TxtHealerAssistMana.Text, s.HealerDpsAboveManaPct, 0, 100);
            s.UseEmergencySelfHealBeforeParty = ChkEmergencySelfHeal.IsChecked == true;
            s.ForceTankDefensiveStanceOrAura = ChkForceTankAura.IsChecked == true;

            s.SoftWaitDistance = ParseFloat(TxtSoftWait.Text, s.SoftWaitDistance, 10f, 150f);
            s.ResumeDistance = ParseFloat(TxtResumeDist.Text, s.ResumeDistance, 5f, 100f);
            s.HardBacktrackDistance = ParseFloat(TxtHardBacktrack.Text, s.HardBacktrackDistance, 20f, 250f);
            s.MaxPullGatherDistance = ParseFloat(TxtMaxPullGather.Text, s.MaxPullGatherDistance, 10f, 100f);
            s.RangedStandoffDistance = ParseFloat(TxtRangedStandoff.Text, s.RangedStandoffDistance, 18f, 24f);
            s.HealerStandoffDistance = ParseFloat(TxtHealerStandoff.Text, s.HealerStandoffDistance, 18f, 24f);
            s.MeleeFlankRadius = ParseFloat(TxtMeleeFlank.Text, s.MeleeFlankRadius, 2.0f, 6.0f);
            if (CmbMountPolicy.SelectedItem is SquadMountPolicy mp)
                s.MountPolicy = mp;

            s.TriggerRestHealthPct = ParseInt(TxtRestTriggerHp.Text, s.TriggerRestHealthPct, 1, 99);
            s.TriggerRestManaPct = ParseInt(TxtRestTriggerMp.Text, s.TriggerRestManaPct, 1, 99);
            s.ResumeRestHealthPct = ParseInt(TxtRestResumeHp.Text, s.ResumeRestHealthPct, 10, 100);
            s.ResumeRestManaPct = ParseInt(TxtRestResumeMp.Text, s.ResumeRestManaPct, 10, 100);

            if (CmbLootMethod.SelectedItem is SquadLootMethod lm)
                s.LootMethod = lm;
            if (CmbRollRule.SelectedItem is SquadRollRule rr)
                s.RollRule = rr;
            s.MaxLootWindowSeconds = ParseInt(TxtMaxLootSec.Text, s.MaxLootWindowSeconds, 1, 30);
            s.SyncTownRuns = ChkSyncTownRuns.IsChecked == true;
            s.TriggerTownFreeBagSlots = ParseInt(TxtTownBagSlots.Text, s.TriggerTownFreeBagSlots, 0, 20);
            s.TriggerTownDurabilityPct = ParseInt(TxtTownDurability.Text, s.TriggerTownDurabilityPct, 0, 99);
            s.TrainSkillsInGroupTownRun = ChkTrainSkills.IsChecked == true;
            s.WaitForPartyRessSeconds = ParseInt(TxtWaitRessSec.Text, s.WaitForPartyRessSeconds, 5, 300);

            s.EnableMageProvisioning = ChkEnableMageProv.IsChecked == true;
            s.PreferMageFoodOverVendor = ChkPreferMageFood.IsChecked == true;
            s.MinMemberFoodStack = ParseInt(TxtMinFood.Text, s.MinMemberFoodStack, 0, 80);
            s.TargetMemberFoodStack = ParseInt(TxtTargetFood.Text, s.TargetMemberFoodStack, 1, 80);
            s.MinMemberDrinkStack = ParseInt(TxtMinDrink.Text, s.MinMemberDrinkStack, 0, 80);
            s.TargetMemberDrinkStack = ParseInt(TxtTargetDrink.Text, s.TargetMemberDrinkStack, 1, 80);
            s.TradeOfferTimeoutSeconds = ParseInt(TxtTradeTimeout.Text, s.TradeOfferTimeoutSeconds, 4, 60);
            s.ProvisionCooldownSeconds = ParseInt(TxtProvisionCooldown.Text, s.ProvisionCooldownSeconds, 5, 300);
            s.EnableWarlockHealthstones = ChkEnableWarlockHs.IsChecked == true;
            s.UseRitualTablesWhenAvailable = ChkUseRitualTables.IsChecked == true;
            s.EnableCrossPartyBuffs = ChkEnableCrossBuffs.IsChecked == true;
            s.SetRaidTargetMarkers = ChkRaidMarkers.IsChecked == true;

            if (s.ResumeRestHealthPct < s.TriggerRestHealthPct || s.ResumeRestManaPct < s.TriggerRestManaPct)
                throw new ArgumentException("Rest resume thresholds must be at least their trigger thresholds.");
            s.GetXML().Save(s.SettingsPath);
        }

        private void CmbRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading)
                return;

            if (CmbRole.SelectedItem is SquadRole role)
            {
                ChkIsCoordinator.IsChecked = role == SquadRole.TankLeader;
            }
        }

        private void CmbClientCount_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading || CmbClientCount.SelectedItem is not int count)
                return;

            string[] defaults = { "TankLeader", "Healer", "DpsMelee", "DpsRanged", "HybridOffHeal" };
            var names = TxtScriptNames.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(count).ToList();
            var roles = TxtScriptRoles.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(count).ToList();
            while (names.Count < count) names.Add($"Char{names.Count + 1}");
            while (roles.Count < count) roles.Add(defaults[roles.Count]);
            TxtScriptNames.Text = string.Join(",", names);
            TxtScriptRoles.Text = string.Join(",", roles);
            GenerateScriptPreview();
        }

        private void BtnGenerateScript_Click(object sender, RoutedEventArgs e)
        {
            GenerateScriptPreview();
        }

        private void BtnCopyScript_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!GenerateScriptPreview()) return;
                WpfClipboard.SetText(TxtGeneratedScript.Text ?? string.Empty);
                WpfMessageBox.Show(this, "Launcher script copied to clipboard.", "Party Grind", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this, "Clipboard error: " + ex.Message, "Party Grind", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var current = SquadSettings.Instance;
                var pending = new SquadSettings(current.SettingsPath);
                pending.LoadFromXML(current.GetXML());
                SaveToSettings(pending);
                SquadSettings.Instance = pending;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show(this, ex.Message, "Party Grind", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private bool GenerateScriptPreview()
        {
            int count = CmbClientCount.SelectedItem is int c ? c : 3;
            string teamId = string.IsNullOrWhiteSpace(TxtTeamId.Text) ? "Squad1" : TxtTeamId.Text.Trim();
            int port = ParseInt(TxtIpcPort.Text, 1339, 1024, 65535);
            string profilePath = string.IsNullOrWhiteSpace(TxtScriptProfilePath.Text)
                ? @"C:\Profiles\Party_1_80.xml"
                : TxtScriptProfilePath.Text.Trim();

            string[] names = (TxtScriptNames.Text ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string[] roles = (TxtScriptRoles.Text ?? "")
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            try
            {
                TxtGeneratedScript.Text = BuildLauncherScript(count, teamId, port, profilePath, names, roles);
                return true;
            }
            catch (ArgumentException ex)
            {
                TxtGeneratedScript.Text = ex.Message;
                return false;
            }
        }

        public static string BuildLauncherScript(
            int clientCount,
            string teamId,
            int port,
            string profilePath,
            IReadOnlyList<string> characterNames,
            IReadOnlyList<string> roleNames)
        {
            foreach (var value in new[] { teamId, profilePath }.Concat(characterNames).Concat(roleNames))
                if (string.IsNullOrEmpty(value) || value.Any(c => char.IsControl(c) || "\"%!=&|<>^()".Contains(c)))
                    throw new ArgumentException("Launcher fields contain unsupported shell characters.");
            if (teamId.Any(char.IsWhiteSpace) || roleNames.Any(r => r.Any(char.IsWhiteSpace)))
                throw new ArgumentException("Team and role names cannot contain whitespace.");
            int count = Math.Clamp(clientCount, 3, 5);
            string[] defaultRoles = { "TankLeader", "Healer", "DpsMelee", "DpsRanged", "HybridOffHeal" };
            var finalNames = new List<string>(count);
            var finalRoles = new List<string>(count);

            for (int i = 0; i < count; i++)
            {
                finalNames.Add(i < characterNames.Count && !string.IsNullOrWhiteSpace(characterNames[i])
                    ? characterNames[i].Trim()
                    : $"Char{i + 1}");
                finalRoles.Add(i < roleNames.Count && !string.IsNullOrWhiteSpace(roleNames[i])
                    ? roleNames[i].Trim()
                    : defaultRoles[i]);
            }

            string rosterCsv = string.Join(",", finalNames);
            string leaderName = finalNames[0];

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine($"REM CopilotBuddy PartyGrind {count}-Client Launcher");
            sb.AppendLine($"set TEAM_ID={teamId}");
            sb.AppendLine($"set IPC_PORT={port}");
            sb.AppendLine($"set PROFILE=\"{profilePath}\"");
            sb.AppendLine();

            for (int i = 0; i < count; i++)
            {
                bool isCoord = i == 0;
                sb.AppendLine(
                    $"start \"Client{i + 1} - {finalNames[i]}\" CopilotBuddy.exe " +
                    $"-instance=Client{i + 1} " +
                    $"-botname=\"Party Grind\" " +
                    $"-loadprofile=%PROFILE% " +
                    $"-squadrole={finalRoles[i]} " +
                    $"-squadcoordinator={(isCoord ? "true" : "false")} " +
                    $"-squadteam=%TEAM_ID% " +
                    $"-squadport=%IPC_PORT% " +
                    $"-squadleader=\"{leaderName}\" " +
                    $"-squadroster=\"{rosterCsv}\"");
                if (i == 0)
                    sb.AppendLine("timeout /t 2 /nobreak >nul");
            }

            return sb.ToString();
        }

        private static int ParseInt(string? raw, int fallback, int min, int max)
        {
            if (int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return Math.Clamp(v, min, max);
            return fallback;
        }

        private static float ParseFloat(string? raw, float fallback, float min, float max)
        {
            if (float.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ||
                float.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                return float.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;
            return fallback;
        }
    }
}
