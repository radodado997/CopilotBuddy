using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CopilotBuddy.Buddy.Overlay;
using CopilotBuddy.Buddy.Overlay.Controls;
using Styx.Helpers;
using Styx.PartyGrind.IPC;

using WpfApplication = System.Windows.Application;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace Styx.PartyGrind
{
    /// <summary>
    /// Live HUD overlay component rendered on StyxWoW.Overlay showing all 3–5 squadmates,
    /// current synchronized phase, sub-profile level band, and lowest member progress.
    /// </summary>
    public sealed class SquadOverlayComponent : OverlayUIComponent
    {
        private readonly OverlayControl _control;
        private readonly TextBlock _headerText;
        private readonly StackPanel _rowsPanel;
        private readonly TextBlock _footerText;

        public override OverlayControl Control => _control;

        public SquadOverlayComponent()
            : base(isHitTestable: true)
        {
            _headerText = new TextBlock
            {
                Text = "PartyGrind HUD",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(120, 200, 255)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 6)
            };

            _rowsPanel = new StackPanel
            {
                Orientation = WpfOrientation.Vertical
            };

            _footerText = new TextBlock
            {
                Text = "SubProfile: [-] | Najniższy: -",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(255, 215, 110)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0)
            };

            var footerBorder = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(200, 0, 90, 158)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 4, 0, 0),
                Child = _footerText
            };

            var rootPanel = new StackPanel
            {
                Orientation = WpfOrientation.Vertical
            };
            rootPanel.Children.Add(_headerText);
            rootPanel.Children.Add(BuildColumnHeaderRow());
            rootPanel.Children.Add(_rowsPanel);
            rootPanel.Children.Add(footerBorder);

            var card = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(215, 24, 24, 28)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(63, 63, 70)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 8, 10, 8),
                Child = rootPanel
            };

            _control = new OverlayControl
            {
                AllowMoving = true,
                AllowResizing = false,
                X = 24,
                Y = 90,
                Width = 580,
                Content = card
            };
        }

        protected internal override void Update()
        {
            var settings = SquadContext.ActiveSettings;
            var leader = SquadContext.IsIpcConnected ? SquadContext.GetLeaderStateSnapshot() : null;
            var local = SquadContext.GetLocalMemberStateSnapshot();

            string team = settings?.TeamId ?? "Squad";
            string phaseText = leader?.Phase.ToString() ?? "Standalone";
            if (leader?.Phase == SquadPhase.TownRun && !string.IsNullOrEmpty(leader.TownRunRequestedByName))
                phaseText += " (" + leader.TownRunRequestedByName + ")";
            string ipcStatus = settings?.IsCoordinator == true ? "Host" : (SquadContext.IsIpcConnected ? "IPC OK" : "Reconnecting");
            _headerText.Text = $"PartyGrind HUD  |  {team} ({ipcStatus})  |  Faza: {phaseText}";

            List<SquadMemberSummary> rows;
            if (leader != null && leader.Members != null && leader.Members.Count > 0)
            {
                rows = leader.Members;
            }
            else if (local != null)
            {
                float xpPct = local.NextLevelXp > 0 ? Math.Clamp(local.CurrentXp * 100f / local.NextLevelXp, 0f, 100f) : 0f;
                rows = new List<SquadMemberSummary>
                {
                    new SquadMemberSummary
                    {
                        CharacterName = local.CharacterName,
                        Role = local.Role,
                        Level = local.Level,
                        XpPercent = xpPct,
                        XpPerHour = local.XpPerHour,
                        HealthPercent = local.HealthPercent,
                        PowerPercent = local.PowerPercent,
                        DistanceToLeader = 0f,
                        IsDead = local.IsDead || local.IsGhost,
                        IsInCombat = local.IsInCombat,
                        IsConnected = true,
                        StateLabel = local.IsDead ? "Dead" : (local.IsInCombat ? "Combat" : "Grinding")
                    }
                };
            }
            else
            {
                rows = new List<SquadMemberSummary>();
            }

            _rowsPanel.Children.Clear();
            foreach (var m in rows)
            {
                _rowsPanel.Children.Add(BuildMemberRow(m));
            }

            if (leader != null)
            {
                string subName = string.IsNullOrEmpty(leader.ActiveSubProfileName) ? "-" : leader.ActiveSubProfileName;
                string lowest = string.IsNullOrEmpty(leader.LowestMemberName) ? "-" : leader.LowestMemberName;
                _footerText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "Aktualny SubProfile: [{0}-{1}] {2}  |  Najniższy gracz: {3} (Lvl {4}, {5:F0}% XP)",
                    leader.ActiveSubProfileMinLevel,
                    leader.ActiveSubProfileMaxLevel,
                    subName,
                    lowest,
                    leader.EffectiveSquadMinLevel,
                    leader.LowestMemberXpPercent);
            }
            else
            {
                int lvl = SquadContext.GetEffectiveProfileLevel();
                _footerText.Text = $"Aktualny SubProfile: -  |  Oczekiwanie na synchronizację (Lvl {lvl})";
            }
        }

        private static Grid BuildColumnHeaderRow()
        {
            var g = CreateColumnsGrid();
            AddCell(g, 0, "Rola", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 1, "Nick", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 2, "Lvl (XP%)", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 3, "XP/h", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 4, "HP / MP", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 5, "Dystans", WpfColors.Silver, FontWeights.SemiBold);
            AddCell(g, 6, "Stan", WpfColors.Silver, FontWeights.SemiBold);
            return g;
        }

        private static Grid BuildMemberRow(SquadMemberSummary m)
        {
            var g = CreateColumnsGrid();
            string roleTag = m.Role switch
            {
                SquadRole.TankLeader => "[Tank]",
                SquadRole.Healer => "[Heal]",
                SquadRole.DpsMelee => "[Melee]",
                SquadRole.DpsRanged => "[Ranged]",
                SquadRole.HybridOffHeal => "[Hybrid]",
                _ => "[?]"
            };

            WpfColor stateColor = m.StateLabel switch
            {
                "Dead" => WpfColor.FromRgb(255, 100, 100),
                "Disconnected" => WpfColor.FromRgb(160, 160, 160),
                "Resting" => WpfColor.FromRgb(100, 210, 255),
                "TownRun" => WpfColor.FromRgb(255, 200, 90),
                "Combat" => WpfColor.FromRgb(255, 150, 80),
                "Provisioning" => WpfColor.FromRgb(210, 150, 255),
                _ => WpfColor.FromRgb(130, 230, 140)
            };

            AddCell(g, 0, roleTag, WpfColors.LightSkyBlue, FontWeights.Normal);
            AddCell(g, 1, string.IsNullOrEmpty(m.CharacterName) ? "-" : m.CharacterName, WpfColors.White, FontWeights.SemiBold);
            AddCell(g, 2, string.Format(CultureInfo.InvariantCulture, "{0} ({1:F0}%)", m.Level, m.XpPercent), WpfColors.White, FontWeights.Normal);
            AddCell(g, 3, FormatXpPerHour(m.XpPerHour), WpfColors.LightGray, FontWeights.Normal);
            AddCell(g, 4, string.Format(CultureInfo.InvariantCulture, "{0:F0}% / {1:F0}%", m.HealthPercent, m.PowerPercent), WpfColors.White, FontWeights.Normal);
            AddCell(g, 5, m.DistanceToLeader < 0 ? "—" : string.Format(CultureInfo.InvariantCulture, "{0:F0} yd", m.DistanceToLeader), WpfColors.LightGray, FontWeights.Normal);
            AddCell(g, 6, string.IsNullOrEmpty(m.StateLabel) ? "Grinding" : m.StateLabel, stateColor, FontWeights.SemiBold);
            return g;
        }

        private static string FormatXpPerHour(float xpPerHour)
        {
            if (xpPerHour >= 1000f)
                return string.Format(CultureInfo.InvariantCulture, "{0:F1}k", xpPerHour / 1000f);
            return string.Format(CultureInfo.InvariantCulture, "{0:F0}", xpPerHour);
        }

        private static Grid CreateColumnsGrid()
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(95) });
            return g;
        }

        private static void AddCell(Grid grid, int column, string text, WpfColor color, FontWeight weight)
        {
            var tb = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 11,
                FontWeight = weight,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(tb, column);
            grid.Children.Add(tb);
        }
    }

    /// <summary>
    /// Attaches and detaches <see cref="SquadOverlayComponent"/> on <see cref="StyxWoW.Overlay"/> safely.
    /// </summary>
    public static class SquadOverlay
    {
        private static readonly object _lock = new object();
        private static SquadOverlayComponent? _component;
        private static CopilotBuddy.Buddy.Overlay.OverlayManager? _manager;

        public static void Attach()
        {
            var app = WpfApplication.Current;
            if (app == null || StyxWoW.Memory == null)
                return;

            try
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    lock (_lock)
                    {
                        if (_component != null)
                            return;

                        var overlay = StyxWoW.Overlay;
                        if (overlay == null || !overlay.IsDesktopCompositionEnabled)
                            return;

                        _component = new SquadOverlayComponent();
                        overlay.AddUIComponent(_component);
                        _manager = overlay;
                    }
                }));
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind Overlay] Could not attach overlay: {0}", ex.Message);
            }
        }

        public static void Detach()
        {
            var app = WpfApplication.Current;
            if (app == null)
            {
                lock (_lock) _component = null;
                return;
            }

            try
            {
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    lock (_lock)
                    {
                        if (_component == null)
                            return;
                        _manager?.RemoveUIComponent(_component);
                        _manager = null;
                        _component = null;
                    }
                }));
            }
            catch (Exception ex)
            {
                Logging.WriteDebug("[PartyGrind Overlay] Could not detach overlay: {0}", ex.Message);
                lock (_lock) _component = null;
            }
        }
    }
}
