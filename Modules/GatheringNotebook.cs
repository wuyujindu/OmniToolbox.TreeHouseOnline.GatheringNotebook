using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Common.Module.Enums;
using OmniToolbox.Common.Module.Models;
using OmniToolbox.Host;
using OmniToolbox.UI;
using OmniToolbox.UI.Controls;
using OmniToolbox.UI.Theme;
using OmenTools.Interop.Game.Lumina;
using OmenTools.OmenService;

namespace OmniToolbox.TreeHouseOnline;

[ModuleApiVersion(1)]
public sealed class GatheringNotebook : OmniModuleBase
{
    public override ModuleInfo Info { get; } = new()
    {
        Category = ModuleCategory.Item,
        Author = "WYJD",
        SupportUrls = ["https://github.com/wuyujindu"]
    };

    public override ModuleLocalization Localization { get; } = ModuleLocalization.Create(
        new Dictionary<string, string>
        {
            ["Title"] = "采集笔记图鉴",
            ["Description"] = "检测角色采集笔记的完成情况, 按采矿与园艺两个分支列出尚未采集过的采集物",
            ["Mining"] = "采矿",
            ["Botany"] = "园艺",
            ["MiningProgress"] = "采矿 已采集 {0} / {1}",
            ["BotanyProgress"] = "园艺 已采集 {0} / {1}",
            ["BranchTab"] = "{0} · 未采集 {1}",
            ["Refresh"] = "刷新",
            ["RefreshHelp"] = "重新读取采集笔记的完成情况",
            ["ShowCollected"] = "显示已采集",
            ["ShowCollectedHelp"] = "勾选后一并列出已经采集过的采集物",
            ["SearchHint"] = "搜索采集物名称",
            ["Name"] = "名称",
            ["Level"] = "等级",
            ["Stars"] = "星级",
            ["AllCollected"] = "这个分支的采集物已经全部采集完成",
            ["NoMatch"] = "没有符合条件的采集物",
            ["NotLoggedIn"] = "登录角色后显示采集笔记进度",
            ["DataLoading"] = "正在读取游戏数据…",
            ["DataUnavailable"] = "游戏数据尚未就绪, 请稍后重试",
            ["SelectAll"] = "全选",
            ["ClearSelection"] = "清除选择",
            ["SelectedCount"] = "已选 {0} 项",
            ["ExportPresets"] = "导出 GBR 预设",
            ["ExportPresetsHelp"] = "把选中的未采集物按每 100 项生成 GatherBuddy Reborn 自动采集预设, 逐个复制后在 GBR 里从剪贴板导入",
            ["PresetDialogTitle"] = "GBR 自动采集预设",
            ["PresetDialogHint"] = "在 GatherBuddy Reborn 的自动采集列表里点「从剪贴板导入」, 一次粘贴一个预设; 每个预设最多 100 项。",
            ["PresetName"] = "{0} 未采集 {1}",
            ["PresetDescription"] = "采集笔记图鉴导出, 共 {0} 项",
            ["PresetItemCount"] = "{0} 项",
            ["Copy"] = "复制",
            ["Copied"] = "已复制: {0}",
            ["SkippedEvents"] = "已跳过 {0} 个活动采集物 (GBR 没有对应采集物)",
            ["Close"] = "关闭",
            ["NoSelection"] = "还没有选择任何采集物"
        });

    private Settings config = new();
    private GatheringCatalog? catalog;
    private bool catalogUnavailable;
    private DateTime catalogRetryAfter = DateTime.MinValue;
    private DateTime lastRefresh = DateTime.MinValue;
    private string search = string.Empty;
    private readonly List<GatheringEntry> filteredEntries = [];
    private readonly List<GbrPreset> presets = [];
    private string copiedPreset = string.Empty;
    private int skippedEventItems;

    public override bool HasSettings => true;

    public override bool DrawSettings()
    {
        if (!EnsureCatalog())
        {
            ImGui.TextUnformatted(Text(catalogUnavailable ? "DataUnavailable" : "DataLoading"));
            return false;
        }

        var loaded = DalamudServices.PlayerState.IsLoaded;
        if (!loaded)
        {
            ImGui.TextUnformatted(Text("NotLoggedIn"));
        }
        else if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 2d)
        {
            RefreshCollected();
        }

        var changed = DrawToolbar();
        DrawProgress();
        ImGui.Spacing();
        DrawBranchTabs();
        DrawPresetPopup();
        return changed;
    }

    public override bool ResetSettings()
    {
        if (!config.ShowCollected)
        {
            return false;
        }

        config.ShowCollected = false;
        return true;
    }

    private bool DrawToolbar()
    {
        var changed = false;

        using (ImRaii.Disabled(!DalamudServices.PlayerState.IsLoaded))
        {
            if (OmniControls.SmallButton(Text("Refresh") + "##gatheringNotebookRefresh", false))
            {
                RefreshCollected();
            }
        }

        OmniControls.HelpTooltip(Text("RefreshHelp"));

        ImGui.SameLine(0f, OmniTheme.Scale(12f));
        var showCollected = config.ShowCollected;
        if (OmniControls.Checkbox(Text("ShowCollected") + "##gatheringNotebookShowCollected", ref showCollected))
        {
            config.ShowCollected = showCollected;
            changed = true;
        }

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        OmniControls.HelpIcon(Text("ShowCollectedHelp"));

        ImGui.SameLine(0f, OmniTheme.Scale(12f));
        ImGui.SetNextItemWidth(OmniTheme.Scale(200f));
        var query = search;
        if (OmniControls.InputTextWithHint("##gatheringNotebookSearch", Text("SearchHint"), ref query, 64))
        {
            search = query;
        }

        ImGui.Spacing();
        var selected = CountSelected();
        ImGui.TextUnformatted(Text("SelectedCount", selected));
        ImGui.SameLine(0f, OmniTheme.Scale(12f));
        using (ImRaii.Disabled(selected == 0))
        {
            if (OmniControls.SmallButton(Text("ExportPresets") + "##gatheringNotebookExport", false))
            {
                BuildPresets();
                copiedPreset = string.Empty;
                ImGui.OpenPopup("##gatheringNotebookPresets");
            }
        }

        OmniControls.HelpTooltip(Text("ExportPresetsHelp"));
        return changed;
    }

    private int CountSelected()
    {
        if (catalog == null)
        {
            return 0;
        }

        return catalog.Mining.Count(entry => entry.Selected) + catalog.Botany.Count(entry => entry.Selected);
    }

    private void DrawProgress()
    {
        if (catalog == null)
        {
            return;
        }

        var miningCollected = catalog.Mining.Count(entry => entry.Collected);
        var botanyCollected = catalog.Botany.Count(entry => entry.Collected);
        ImGui.TextUnformatted(Text("MiningProgress", miningCollected, catalog.Mining.Count));
        ImGui.SameLine(0f, OmniTheme.Scale(18f));
        ImGui.TextUnformatted(Text("BotanyProgress", botanyCollected, catalog.Botany.Count));
    }

    private void DrawBranchTabs()
    {
        if (catalog == null)
        {
            return;
        }

        using var tabs = ImRaii.TabBar("##gatheringNotebookTabs");
        if (!tabs)
        {
            return;
        }

        using (var miningTab = ImRaii.TabItem(Text("BranchTab", Text("Mining"), CountPending(catalog.Mining))))
        {
            if (miningTab)
            {
                DrawBranch(catalog.Mining, "##gatheringNotebookMiningTable");
            }
        }

        using (var botanyTab = ImRaii.TabItem(Text("BranchTab", Text("Botany"), CountPending(catalog.Botany))))
        {
            if (botanyTab)
            {
                DrawBranch(catalog.Botany, "##gatheringNotebookBotanyTable");
            }
        }
    }

    private void DrawBranch(List<GatheringEntry> entries, string tableId)
    {
        filteredEntries.Clear();
        filteredEntries.AddRange(Filter(entries, config.ShowCollected, search));

        if (filteredEntries.Count == 0)
        {
            ImGui.TextUnformatted(Text(entries.Count > 0 && entries.All(entry => entry.Collected) ? "AllCollected" : "NoMatch"));
            return;
        }

        if (OmniControls.SmallButton(Text("SelectAll") + "##selectAll" + tableId, false))
        {
            foreach (var entry in filteredEntries)
            {
                entry.Selected = true;
            }
        }

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        if (OmniControls.SmallButton(Text("ClearSelection") + "##clearSelection" + tableId, false))
        {
            foreach (var entry in filteredEntries)
            {
                entry.Selected = false;
            }
        }

        ImGui.SameLine(0f, OmniTheme.Scale(12f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Text("SelectedCount", entries.Count(entry => entry.Selected)));

        using var table = ImRaii.Table(
            tableId,
            4,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(-1f, OmniTheme.Scale(320f)));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("##select", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(34f));
        ImGui.TableSetupColumn(Text("Name"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Text("Level"), ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(70f));
        ImGui.TableSetupColumn(Text("Stars"), ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(70f));
        ImGui.TableHeadersRow();

        var iconSize = OmniTheme.Scale(22f);
        foreach (var entry in filteredEntries)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            var selected = entry.Selected;
            if (ImGui.Checkbox($"##gatheringNotebookSelect{entry.GatheringItemId}", ref selected))
            {
                entry.Selected = selected;
            }

            ImGui.TableNextColumn();
            var iconPosition = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(iconSize));
            ImGui.SameLine(0f, OmniTheme.Scale(6f));
            if (entry.IconId > 0 && ImageHelper.GetGameIcon(entry.IconId) is { } texture)
            {
                ImGui.GetWindowDrawList().AddImage(texture.Handle, iconPosition, iconPosition + new Vector2(iconSize));
            }

            ImGui.AlignTextToFramePadding();
            var color = entry.Collected ? new Vector4(0.62f, 0.62f, 0.66f, 1f) : OmniTheme.Tokens.Text;
            using (ImRaii.PushColor(ImGuiCol.Text, color, true))
            {
                ImGui.TextUnformatted(entry.Name);
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.Level.ToString(CultureInfo.InvariantCulture));

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(entry.Stars == 0 ? "—" : new string('★', entry.Stars));
        }
    }

    private static int CountPending(List<GatheringEntry> entries) => entries.Count(entry => !entry.Collected);

    internal const int GbrPresetItemLimit = 100;
    internal const byte GbrPresetVersion = 6;

    private void BuildPresets()
    {
        presets.Clear();
        skippedEventItems = 0;
        if (catalog == null)
        {
            return;
        }

        skippedEventItems = catalog.Mining.Count(entry => entry.Selected && entry.IsEventItem)
                            + catalog.Botany.Count(entry => entry.Selected && entry.IsEventItem);
        presets.AddRange(BuildPresets(
            catalog.Mining,
            catalog.Botany,
            Text("Mining"),
            Text("Botany"),
            Text("PresetName"),
            Text("PresetDescription")));
    }

    internal static List<GbrPreset> BuildPresets(
        IEnumerable<GatheringEntry> mining,
        IEnumerable<GatheringEntry> botany,
        string miningLabel,
        string botanyLabel,
        string nameFormat,
        string descriptionFormat)
    {
        var result = new List<GbrPreset>();
        var order = 1;
        AppendPresets(result, mining, miningLabel, nameFormat, descriptionFormat, ref order);
        AppendPresets(result, botany, botanyLabel, nameFormat, descriptionFormat, ref order);
        return result;
    }

    private static void AppendPresets(
        List<GbrPreset> result,
        IEnumerable<GatheringEntry> entries,
        string label,
        string nameFormat,
        string descriptionFormat,
        ref int order)
    {
        var itemIds = entries
            .Where(entry => entry.Selected && !entry.IsEventItem)
            .Select(entry => entry.ItemId)
            .ToList();
        for (var offset = 0; offset < itemIds.Count; offset += GbrPresetItemLimit)
        {
            var chunk = itemIds.GetRange(offset, Math.Min(GbrPresetItemLimit, itemIds.Count - offset));
            var index = offset / GbrPresetItemLimit + 1;
            var name = string.Format(nameFormat, label, index);
            var description = string.Format(descriptionFormat, chunk.Count);
            result.Add(new GbrPreset(name, description, chunk, EncodePresetBase64(order, name, description, chunk)));
            order++;
        }
    }

    internal static string EncodePresetBase64(int order, string name, string description, IReadOnlyList<uint> itemIds)
    {
        var payload = new GbrPresetPayload
        {
            Name = name,
            Description = description,
            Order = order,
            ItemIds = itemIds.ToArray()
        };

        foreach (var itemId in itemIds)
        {
            payload.Quantities[itemId] = 1;
            payload.EnabledItems[itemId] = true;
        }

        var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        var buffer = new byte[jsonBytes.Length + 1];
        buffer[0] = GbrPresetVersion;
        Array.Copy(jsonBytes, 0, buffer, 1, jsonBytes.Length);

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
        {
            gzip.Write(buffer, 0, buffer.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    private void DrawPresetPopup()
    {
        using var popup = ImRaii.Popup("##gatheringNotebookPresets");
        if (!popup)
        {
            return;
        }

        ImGui.TextUnformatted(Text("PresetDialogTitle"));
        ImGui.TextWrapped(Text("PresetDialogHint"));
        if (skippedEventItems > 0)
        {
            ImGui.TextUnformatted(Text("SkippedEvents", skippedEventItems));
        }

        ImGui.Separator();
        if (presets.Count == 0)
        {
            ImGui.TextUnformatted(Text("NoSelection"));
        }

        for (var index = 0; index < presets.Count; index++)
        {
            var preset = presets[index];
            ImGui.TextUnformatted(preset.Name);
            ImGui.SameLine(0f, OmniTheme.Scale(8f));
            ImGui.TextUnformatted(Text("PresetItemCount", preset.ItemIds.Count));
            ImGui.SameLine(0f, OmniTheme.Scale(8f));
            if (OmniControls.SmallButton(Text("Copy") + "##gatheringNotebookCopy" + index, false))
            {
                ImGui.SetClipboardText(preset.Base64);
                copiedPreset = preset.Name;
            }
        }

        if (copiedPreset.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(Text("Copied", copiedPreset));
        }

        ImGui.Spacing();
        if (OmniControls.SmallButton(Text("Close") + "##gatheringNotebookClosePresets", false))
        {
            ImGui.CloseCurrentPopup();
        }
    }

    private void RefreshCollected()
    {
        if (catalog == null)
        {
            return;
        }

        var loaded = DalamudServices.PlayerState.IsLoaded;
        foreach (var entry in catalog.Mining)
        {
            entry.Collected = loaded && QuestManager.IsGatheringItemGathered((ushort)entry.GatheringItemId);
        }

        foreach (var entry in catalog.Botany)
        {
            entry.Collected = loaded && QuestManager.IsGatheringItemGathered((ushort)entry.GatheringItemId);
        }

        lastRefresh = DateTime.UtcNow;
    }

    private bool EnsureCatalog()
    {
        if (catalog != null)
        {
            return true;
        }

        if (catalogUnavailable && DateTime.UtcNow < catalogRetryAfter)
        {
            return false;
        }

        try
        {
            catalog = GatheringCatalog.Build();
            catalogUnavailable = false;
            RefreshCollected();
            return true;
        }
        catch (Exception exception)
        {
            DalamudServices.PluginLog.Debug(exception, "[采集笔记图鉴] 读取游戏数据失败");
            catalogUnavailable = true;
            catalogRetryAfter = DateTime.UtcNow.AddSeconds(5);
            return false;
        }
    }

    internal static List<GatheringEntry> Filter(IEnumerable<GatheringEntry> entries, bool showCollected, string query)
    {
        var search = (query ?? string.Empty).Trim();
        var result = new List<GatheringEntry>();
        foreach (var entry in entries)
        {
            if (!showCollected && entry.Collected)
            {
                continue;
            }

            if (search.Length > 0 && entry.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    internal static GatheringBranch ClassifyGatheringType(uint gatheringTypeId) => gatheringTypeId switch
    {
        0 or 1 => GatheringBranch.Mining,
        2 or 3 => GatheringBranch.Botany,
        _ => GatheringBranch.None
    };

    internal static int CompareEntries(GatheringEntry left, GatheringEntry right)
    {
        var byLevel = left.Level.CompareTo(right.Level);
        return byLevel != 0 ? byLevel : string.Compare(left.Name, right.Name, StringComparison.CurrentCulture);
    }

    internal string Text(string key, params object[] arguments)
    {
        var value = OmniLoc.Get(this, key);
        return arguments.Length == 0 ? value : string.Format(value, arguments);
    }

    [Serializable]
    internal sealed class Settings
    {
        public bool ShowCollected { get; set; }
    }

    internal sealed class GatheringCatalog
    {
        public List<GatheringEntry> Mining { get; } = [];
        public List<GatheringEntry> Botany { get; } = [];

        public static GatheringCatalog Build()
        {
            var result = new GatheringCatalog();
            var branches = new Dictionary<uint, GatheringBranch>();
            var gatheringItems = new Dictionary<uint, GatheringItem>();
            foreach (var row in LuminaGetter.Get<GatheringItem>())
            {
                gatheringItems[row.RowId] = row;
            }

            foreach (var pointBase in LuminaGetter.Get<GatheringPointBase>())
            {
                var branch = ClassifyGatheringType(pointBase.GatheringType.RowId);
                if (branch == GatheringBranch.None)
                {
                    continue;
                }

                for (var index = 0; index < pointBase.Item.Count; index++)
                {
                    var gatheringItemId = pointBase.Item[index].RowId;
                    if (gatheringItemId == 0 || gatheringItemId > ushort.MaxValue)
                    {
                        continue;
                    }

                    branches[gatheringItemId] = branches.TryGetValue(gatheringItemId, out var existing)
                        ? existing | branch
                        : branch;
                }
            }

            foreach (var (gatheringItemId, branch) in branches)
            {
                if (!gatheringItems.TryGetValue(gatheringItemId, out var gatheringItem))
                {
                    continue;
                }

                var itemId = gatheringItem.Item.RowId;
                if (itemId == 0)
                {
                    continue;
                }

                string name;
                uint iconId;
                var isEventItem = false;
                if (gatheringItem.Item.TryGetValue<Item>(out var item))
                {
                    name = item.Name.ExtractText();
                    iconId = item.Icon;
                }
                else if (gatheringItem.Item.TryGetValue<EventItem>(out var eventItem))
                {
                    name = eventItem.Singular.ExtractText();
                    iconId = eventItem.Icon;
                    isEventItem = true;
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                byte level = 0;
                byte stars = 0;
                if (gatheringItem.GatheringItemLevel.TryGetValue(out var itemLevel))
                {
                    level = itemLevel.GatheringItemLevel;
                    stars = itemLevel.Stars;
                }

                var entry = new GatheringEntry
                {
                    GatheringItemId = gatheringItemId,
                    ItemId = itemId,
                    IconId = iconId,
                    Name = name,
                    Level = level,
                    Stars = stars,
                    IsEventItem = isEventItem
                };

                if (branch.HasFlag(GatheringBranch.Mining))
                {
                    result.Mining.Add(entry);
                }

                if (branch.HasFlag(GatheringBranch.Botany))
                {
                    result.Botany.Add(entry);
                }
            }

            result.Mining.Sort(CompareEntries);
            result.Botany.Sort(CompareEntries);
            return result;
        }
    }
}

internal sealed class GatheringEntry
{
    public uint GatheringItemId { get; init; }
    public uint ItemId { get; init; }
    public uint IconId { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Level { get; init; }
    public byte Stars { get; init; }
    public bool IsEventItem { get; init; }
    public bool Collected { get; set; }
    public bool Selected { get; set; }
}

internal sealed class GbrPreset(string name, string description, List<uint> itemIds, string base64)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<uint> ItemIds { get; } = itemIds;
    public string Base64 { get; } = base64;
}

internal sealed class GbrPresetPayload
{
    public byte CurrentVersion { get; set; } = GatheringNotebook.GbrPresetVersion;
    public uint[] ItemIds { get; set; } = [];
    public Dictionary<uint, uint> Quantities { get; set; } = [];
    public Dictionary<uint, uint> PrefferedLocations { get; set; } = [];
    public Dictionary<uint, bool> EnabledItems { get; set; } = [];
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public int Order { get; set; }
    public bool Enabled { get; set; }
    public bool Fallback { get; set; }
    public bool RemoveCompletedItems { get; set; }
}

[Flags]
internal enum GatheringBranch
{
    None = 0,
    Mining = 1,
    Botany = 2
}
