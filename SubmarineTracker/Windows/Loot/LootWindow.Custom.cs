﻿using Lumina.Excel.GeneratedSheets;
using SubmarineTracker.Data;

namespace SubmarineTracker.Windows.Loot;

public partial class LootWindow
{
    private void CustomLootTab()
    {
        if (ImGui.BeginTabItem("Custom"))
        {
            if (!Configuration.CustomLootWithValue.Any())
            {
                ImGui.TextColored(ImGuiColors.ParsedOrange, "No Custom Loot");
                ImGui.TextColored(ImGuiColors.ParsedOrange, "You can add selected items via the loot tab under settings.");

                ImGui.EndTabItem();
                return;
            }

            ImGuiHelpers.ScaledDummy(5.0f);

            var numSubs = 0;
            var numVoyages = 0;
            var moneyMade = 0L;
            var bigList = new Dictionary<Item, int>();
            foreach (var fc in Submarines.KnownSubmarines.Values)
            {
                fc.RebuildStats(Configuration.ExcludeLegacy);
                var dateLimit = DateUtil.LimitToDate(Configuration.DateLimit);

                numSubs += fc.Submarines.Count;
                numVoyages += fc.SubLoot.Values.SelectMany(subLoot => subLoot.Loot
                                                                             .Where(loot => loot.Value.First().Date >= dateLimit)
                                                                             .Where(loot => !Configuration.ExcludeLegacy || loot.Value.First().Valid)).Count();

                foreach (var (item, count) in fc.TimeLoot.Where(r => r.Key >= dateLimit).SelectMany(kv => kv.Value))
                {
                    if (!Configuration.CustomLootWithValue.ContainsKey(item.RowId))
                        continue;

                    if (!bigList.ContainsKey(item))
                    {
                        bigList.Add(item, count);
                    }
                    else
                    {
                        bigList[item] += count;
                    }
                }
            }

            if (!bigList.Any())
            {
                ImGui.TextColored(ImGuiColors.ParsedOrange, Configuration.DateLimit != DateLimit.None
                                                                ? "None of the selected items have been looted in the time frame."
                                                                : "None of the selected items have been looted yet.");
                ImGui.EndTabItem();
                return;
            }

            var textHeight = ImGui.CalcTextSize("XXXX").Y * 4.2f; // giving space for 4.2 lines
            if (ImGui.BeginChild("##customLootTableChild", new Vector2(0, -textHeight)))
            {
                if (ImGui.BeginTable($"##customLootTable", 3))
                {
                    ImGui.TableSetupColumn("##icon", 0, 0.15f);
                    ImGui.TableSetupColumn("##item");
                    ImGui.TableSetupColumn("##amount", 0, 0.3f);

                    foreach (var (item, count) in bigList)
                    {
                        ImGui.TableNextColumn();
                        DrawIcon(item.Icon);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(Utils.ToStr(item.Name));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted($"{count}");
                        ImGui.TableNextRow();

                        moneyMade += count * Configuration.CustomLootWithValue[item.RowId];
                    }
                }

                ImGui.EndTable();
            }

            ImGui.EndChild();

            if (ImGui.BeginChild("##customLootTextChild", new Vector2(0, 0), false, 0))
            {
                var limit = Configuration.DateLimit != DateLimit.None
                                ? $"over {DateUtil.GetDateLimitName(Configuration.DateLimit)}"
                                : "";
                ImGui.TextWrapped($"The above rewards have been obtained {limit} from a total of {numVoyages} voyages via {numSubs} submarines.");
                ImGui.TextWrapped($"This made you a total of {moneyMade:N0} gil.");
            }

            ImGui.EndChild();

            ImGui.EndTabItem();
        }
    }
}
