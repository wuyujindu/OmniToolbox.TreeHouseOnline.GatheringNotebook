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
            ["LevelSection"] = "采集等级",
            ["SpecialSection"] = "特殊分类",
            ["DetailTitle"] = "采集点",
            ["NodeLine"] = "{0} · {1} {2}级",
            ["CollectableTag"] = "收藏品",
            ["StarsTag"] = "★ {0}",
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
            ["RuriOrder"] = "生成 Ruri 订单",
            ["RuriOrderHelp"] = "把选中的未采集物生成 Ruri 订单, 打开 Ruri 并做计划检查 (不会开始执行)",
            ["RuriStart"] = "Ruri 立即采集",
            ["RuriStartHelp"] = "把选中的未采集物生成 Ruri 订单, 打开 Ruri 并直接开始采集",
            ["RuriSent"] = "已向 Ruri 发送 {0} 个订单",
            ["RuriPlanSent"] = "已向 Ruri 发送 {0} 个订单并开始计划检查",
            ["RuriFailed"] = "无法调用 Ruri: 命令 /ruri 不可用, 请确认琉璃的宝石插件已启用",
            ["PresetDialogTitle"] = "GBR 自动采集预设",
            ["PresetDialogHint"] = "在 GatherBuddy Reborn 的自动采集列表里点「从剪贴板导入」, 一次粘贴一个预设; 每个预设最多 100 项。",
            ["PresetName"] = "{0} 未采集 {1}",
            ["PresetDescription"] = "采集笔记图鉴导出, 共 {0} 项",
            ["PresetItemCount"] = "{0} 项",
            ["Copy"] = "复制",
            ["Copied"] = "已复制: {0}",
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
    private GatheringBranch activeBranch = GatheringBranch.Mining;
    private string selectedCategoryKey = string.Empty;
    private GatheringEntry? detailEntry;
    private string copiedPreset = string.Empty;

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
        DrawBody();
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

        ImGui.SameLine(0f, OmniTheme.Scale(12f));
        using (ImRaii.Disabled(selected == 0))
        {
            if (OmniControls.SmallButton(Text("RuriOrder") + "##gatheringNotebookRuriOrder", false))
            {
                SendRuriOrders(false);
            }
        }

        OmniControls.HelpTooltip(Text("RuriOrderHelp"));

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        using (ImRaii.Disabled(selected == 0))
        {
            if (OmniControls.SmallButton(Text("RuriStart") + "##gatheringNotebookRuriStart", false))
            {
                SendRuriOrders(true);
            }
        }

        OmniControls.HelpTooltip(Text("RuriStartHelp"));
        return changed;
    }

    private void SendRuriOrders(bool start)
    {
        if (catalog == null)
        {
            return;
        }

        var entries = catalog.Mining.Concat(catalog.Botany).ToList();
        var json = BuildRuriOrderJson(entries);
        if (json.Length == 0)
        {
            return;
        }

        ImGui.SetClipboardText(json);

        var commandManager = DalamudServices.CommandManager;
        if (!commandManager.ProcessCommand("/ruri"))
        {
            DalamudServices.ChatGUI.Print(Text("RuriFailed"));
            return;
        }

        var count = entries.Where(entry => entry.Selected).Select(entry => entry.ItemId).Distinct().Count();
        if (commandManager.ProcessCommand((start ? "/ruri run " : "/ruri dryrun ") + json))
        {
            DalamudServices.ChatGUI.Print(Text(start ? "RuriSent" : "RuriPlanSent", count));
        }
        else
        {
            DalamudServices.ChatGUI.Print(Text("RuriFailed"));
        }
    }

    internal static string BuildRuriOrderJson(IEnumerable<GatheringEntry> entries)
    {
        var orders = new List<RuriOrderJson>();
        var seen = new HashSet<uint>();
        foreach (var entry in entries)
        {
            if (!entry.Selected || !seen.Add(entry.ItemId))
            {
                continue;
            }

            orders.Add(new RuriOrderJson { ItemId = entry.ItemId });
        }

        return orders.Count == 0 ? string.Empty : JsonSerializer.Serialize(orders);
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

    private void DrawBody()
    {
        if (catalog == null)
        {
            return;
        }

        var entries = activeBranch == GatheringBranch.Mining ? catalog.Mining : catalog.Botany;
        var categories = BuildCategories(entries);
        if (categories.Count == 0)
        {
            ImGui.TextUnformatted(Text("NoMatch"));
            return;
        }

        if (categories.All(category => category.Key != selectedCategoryKey))
        {
            selectedCategoryKey = categories[0].Key;
        }

        var category = categories.First(item => item.Key == selectedCategoryKey);
        var height = OmniTheme.Scale(430f);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(OmniTheme.Scale(10f), OmniTheme.Scale(8f))))
        {
            using (var sidebar = ImRaii.Child("##gatheringNotebookSidebar", new Vector2(OmniTheme.Scale(200f), height), true))
            {
                if (sidebar)
                {
                    DrawSidebar(entries, categories);
                }
            }

            ImGui.SameLine(0f, OmniTheme.Scale(8f));
            using (var list = ImRaii.Child("##gatheringNotebookList", new Vector2(OmniTheme.Scale(345f), height), true))
            {
                if (list)
                {
                    DrawList(entries, category);
                }
            }

            ImGui.SameLine(0f, OmniTheme.Scale(8f));
            using (var detail = ImRaii.Child("##gatheringNotebookDetail", new Vector2(-1f, height), true))
            {
                if (detail)
                {
                    DrawDetail();
                }
            }
        }
    }

    private void DrawSidebar(List<GatheringEntry> entries, List<GatheringCategory> categories)
    {
        var buttonWidth = (ImGui.GetContentRegionAvail().X - OmniTheme.Scale(6f)) * 0.5f;
        if (OmniControls.SmallButton(Text("Mining") + "##gatheringNotebookMining", activeBranch == GatheringBranch.Mining, new Vector2(buttonWidth, 0f)))
        {
            SwitchBranch(GatheringBranch.Mining);
        }

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        if (OmniControls.SmallButton(Text("Botany") + "##gatheringNotebookBotany", activeBranch == GatheringBranch.Botany, new Vector2(buttonWidth, 0f)))
        {
            SwitchBranch(GatheringBranch.Botany);
        }

        ImGui.Spacing();
        if (DrawSectionHeader("LevelSection", "##gatheringNotebookLevels"))
        {
            foreach (var category in categories.Where(item => !item.IsSpecial))
            {
                DrawCategoryRow(entries, category);
            }
        }

        if (!categories.Any(item => item.IsSpecial))
        {
            return;
        }

        ImGui.Spacing();
        if (DrawSectionHeader("SpecialSection", "##gatheringNotebookSpecial"))
        {
            foreach (var category in categories.Where(item => item.IsSpecial))
            {
                DrawCategoryRow(entries, category);
            }
        }
    }

    private bool DrawSectionHeader(string key, string id)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, OmniTheme.ControlAccent, true))
        {
            ImGui.SetWindowFontScale(1.15f);
            var open = ImGui.CollapsingHeader(Text(key) + id, ImGuiTreeNodeFlags.DefaultOpen);
            ImGui.SetWindowFontScale(1f);
            return open;
        }
    }

    private void DrawCategoryRow(List<GatheringEntry> entries, GatheringCategory category)
    {
        var pending = entries.Count(entry => !entry.Collected && category.Matches(entry));
        var label = $"{category.Label} ({pending})";
        var available = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(label).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (available - textWidth) * 0.5f));
        var clicked = ImGui.Selectable($"{label}##{category.Key}", category.Key == selectedCategoryKey);
        if (textWidth > available)
        {
            OmniControls.HelpTooltip(label, true);
        }

        if (clicked)
        {
            selectedCategoryKey = category.Key;
        }
    }

    private void DrawList(List<GatheringEntry> entries, GatheringCategory category)
    {
        filteredEntries.Clear();
        filteredEntries.AddRange(Filter(entries, config.ShowCollected, search, category));

        if (filteredEntries.Count == 0)
        {
            ImGui.TextUnformatted(Text(entries.Count > 0 && entries.All(entry => entry.Collected) ? "AllCollected" : "NoMatch"));
            return;
        }

        if (OmniControls.SmallButton(Text("SelectAll") + "##gatheringNotebookSelectAll", false))
        {
            foreach (var entry in filteredEntries)
            {
                entry.Selected = true;
            }
        }

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        if (OmniControls.SmallButton(Text("ClearSelection") + "##gatheringNotebookClearSelection", false))
        {
            foreach (var entry in filteredEntries)
            {
                entry.Selected = false;
            }
        }

        ImGui.SameLine(0f, OmniTheme.Scale(10f));
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(Text("SelectedCount", entries.Count(entry => entry.Selected)));

        using var table = ImRaii.Table(
            "##gatheringNotebookItems",
            3,
            ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY,
            new Vector2(-1f, -1f));
        if (!table)
        {
            return;
        }

        ImGui.TableSetupColumn("##select", ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(30f));
        ImGui.TableSetupColumn(Text("Name"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(Text("Level"), ImGuiTableColumnFlags.WidthFixed, OmniTheme.Scale(60f));

        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        string[] headers = [string.Empty, Text("Name"), Text("Level")];
        for (var column = 0; column < headers.Length; column++)
        {
            ImGui.TableSetColumnIndex(column);
            if (headers[column].Length == 0)
            {
                ImGui.TableHeader(string.Empty);
                continue;
            }

            var headerWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (headerWidth - ImGui.CalcTextSize(headers[column]).X) * 0.5f));
            ImGui.TableHeader(headers[column]);
        }

        var iconSize = OmniTheme.Scale(20f);
        var iconSpacing = OmniTheme.Scale(6f);
        foreach (var entry in filteredEntries)
        {
            ImGui.TableNextRow();
            ImGui.PushID((int)entry.ItemId);

            ImGui.TableNextColumn();
            var checkboxWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (checkboxWidth - ImGui.GetFrameHeight()) * 0.5f));
            var selected = entry.Selected;
            if (ImGui.Checkbox("##gatheringNotebookSelect", ref selected))
            {
                entry.Selected = selected;
            }

            ImGui.TableNextColumn();
            var cellWidth = ImGui.GetContentRegionAvail().X;
            var nameWidth = ImGui.CalcTextSize(entry.Name).X;
            var groupWidth = iconSize + iconSpacing + nameWidth;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (cellWidth - groupWidth) * 0.5f));

            var iconPosition = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new Vector2(iconSize));
            ImGui.SameLine(0f, iconSpacing);
            if (entry.IconId > 0 && ImageHelper.GetGameIcon(entry.IconId) is { } texture)
            {
                ImGui.GetWindowDrawList().AddImage(texture.Handle, iconPosition, iconPosition + new Vector2(iconSize));
            }

            var color = entry.Collected ? new Vector4(0.62f, 0.62f, 0.66f, 1f) : OmniTheme.Tokens.Text;
            using (ImRaii.PushColor(ImGuiCol.Text, color, true))
            {
                if (ImGui.Selectable($"{entry.Name}##gatheringNotebookItem", detailEntry == entry))
                {
                    detailEntry = entry;
                }
            }

            if (ImGui.IsItemHovered())
            {
                detailEntry = entry;
            }

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            var levelText = entry.Level.ToString(CultureInfo.InvariantCulture);
            var levelWidth = ImGui.GetContentRegionAvail().X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + MathF.Max(0f, (levelWidth - ImGui.CalcTextSize(levelText).X) * 0.5f));
            ImGui.TextUnformatted(levelText);

            ImGui.PopID();
        }
    }

    private void DrawDetail()
    {
        var entry = detailEntry;
        if (entry == null)
        {
            ImGui.TextUnformatted(Text("NoSelection"));
            return;
        }

        DrawCenteredText(entry.Name);
        if (entry.IsCollectable)
        {
            DrawCenteredText($"[{Text("CollectableTag")}]");
        }

        var levelLine = $"{Text("Level")} {entry.Level.ToString(CultureInfo.InvariantCulture)}";
        if (entry.Stars > 0)
        {
            levelLine += $"  {Text("StarsTag", entry.Stars)}";
        }

        DrawCenteredText(levelLine);
        ImGui.Separator();
        DrawCenteredText(Text("DetailTitle"));

        foreach (var node in entry.Nodes.Take(10))
        {
            DrawCenteredText(Text("NodeLine", node.Territory, node.Place, node.Level));
        }
    }

    private static void DrawCenteredText(string text)
    {
        var available = ImGui.GetContentRegionAvail().X;
        var textWidth = ImGui.CalcTextSize(text).X;
        if (textWidth < available)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (available - textWidth) * 0.5f);
        }

        ImGui.TextUnformatted(text);
    }

    private List<GatheringCategory> BuildCategories(List<GatheringEntry> entries)
    {
        var result = new List<GatheringCategory>();
        for (var max = 100; max > 0; max -= 5)
        {
            var min = max - 4;
            if (entries.All(entry => entry.Level < min || entry.Level > max))
            {
                continue;
            }

            result.Add(new GatheringCategory
            {
                Key = $"level:{min}-{max}",
                Label = $"{min}-{max}",
                MinLevel = (byte)min,
                MaxLevel = (byte)max
            });
        }

        if (entries.Any(entry => entry.IsCollectable))
        {
            result.Add(new GatheringCategory
            {
                Key = "collectable",
                Label = Text("CollectableTag"),
                IsSpecial = true,
                CollectableOnly = true
            });
        }

        var specials = entries
            .SelectMany(entry => entry.SpecialCategories)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(label => label, StringComparer.CurrentCulture);
        foreach (var special in specials)
        {
            result.Add(new GatheringCategory
            {
                Key = "special:" + special,
                Label = special,
                IsSpecial = true,
                SpecialLabel = special
            });
        }

        return result;
    }

    private void SwitchBranch(GatheringBranch branch)
    {
        if (activeBranch == branch)
        {
            return;
        }

        activeBranch = branch;
        selectedCategoryKey = string.Empty;
        detailEntry = null;
    }

    internal const int GbrPresetItemLimit = 100;
    internal const byte GbrPresetVersion = 6;

    private void BuildPresets()
    {
        presets.Clear();
        if (catalog == null)
        {
            return;
        }

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
        var exported = new HashSet<uint>();
        var order = 1;
        AppendPresets(result, mining, miningLabel, nameFormat, descriptionFormat, exported, ref order);
        AppendPresets(result, botany, botanyLabel, nameFormat, descriptionFormat, exported, ref order);
        return result;
    }

    private static void AppendPresets(
        List<GbrPreset> result,
        IEnumerable<GatheringEntry> entries,
        string label,
        string nameFormat,
        string descriptionFormat,
        HashSet<uint> exported,
        ref int order)
    {
        var itemIds = new List<uint>();
        foreach (var entry in entries)
        {
            if (!entry.Selected)
            {
                continue;
            }

            // 同一物品可能同时属于采矿和园艺，只导出一次。
            if (exported.Add(entry.ItemId))
            {
                itemIds.Add(entry.ItemId);
            }
        }
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
            entry.Collected = loaded && IsAnyGathered(entry);
        }

        foreach (var entry in catalog.Botany)
        {
            entry.Collected = loaded && IsAnyGathered(entry);
        }

        lastRefresh = DateTime.UtcNow;
    }

    private static bool IsAnyGathered(GatheringEntry entry)
    {
        foreach (var gatheringItemId in entry.GatheringItemIds)
        {
            if (QuestManager.IsGatheringItemGathered((ushort)gatheringItemId))
            {
                return true;
            }
        }

        return false;
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

    internal static List<GatheringEntry> Filter(
        IEnumerable<GatheringEntry> entries,
        bool showCollected,
        string query,
        GatheringCategory? category)
    {
        var search = (query ?? string.Empty).Trim();
        var result = new List<GatheringEntry>();
        foreach (var entry in entries)
        {
            if (category != null && !category.Matches(entry))
            {
                continue;
            }

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
            var gatheringItems = new Dictionary<uint, GatheringItem>();
            foreach (var row in LuminaGetter.Get<GatheringItem>())
            {
                gatheringItems[row.RowId] = row;
            }

            var pointBases = new Dictionary<uint, GatheringPointBase>();
            foreach (var row in LuminaGetter.Get<GatheringPointBase>())
            {
                pointBases[row.RowId] = row;
            }

            // 有常规采集点的 base：GBR 只把这些物品当作可自动采集；顺便记录采集点与传承录分类。
            var subCategories = new Dictionary<uint, GatheringSubCategory>();
            foreach (var row in LuminaGetter.Get<GatheringSubCategory>())
            {
                subCategories[row.RowId] = row;
            }

            var liveBases = new HashSet<uint>();
            var baseNodes = new Dictionary<uint, List<GatheringNodeInfo>>();
            var baseSpecials = new Dictionary<uint, HashSet<string>>();
            foreach (var point in LuminaGetter.Get<GatheringPoint>())
            {
                if (point.TerritoryType.RowId <= 1)
                {
                    continue;
                }

                var baseId = point.GatheringPointBase.RowId;
                if (baseId == 0 || !pointBases.TryGetValue(baseId, out var liveBase))
                {
                    continue;
                }

                if (liveBase.GatheringType.RowId >= 4 || liveBase.Item.Count == 0)
                {
                    continue;
                }

                liveBases.Add(baseId);

                if (!point.TerritoryType.TryGetValue(out var territoryRow))
                {
                    continue;
                }

                var territory = territoryRow.PlaceName.TryGetValue(out var placeNameRow)
                    ? placeNameRow.Name.ExtractText()
                    : string.Empty;
                var place = point.PlaceName.TryGetValue(out var pointPlaceName)
                    ? pointPlaceName.Name.ExtractText()
                    : string.Empty;
                var node = new GatheringNodeInfo
                {
                    Territory = territory,
                    Place = place,
                    Level = liveBase.GatheringLevel
                };

                if (!baseNodes.TryGetValue(baseId, out var nodes))
                {
                    baseNodes[baseId] = nodes = [];
                }

                if (!nodes.Any(existing => existing.Key == node.Key))
                {
                    nodes.Add(node);
                }

                var subCategoryId = point.GatheringSubCategory.RowId;
                if (subCategoryId == 0 || !subCategories.TryGetValue(subCategoryId, out var subCategory) ||
                    subCategory.Division < 2000)
                {
                    continue;
                }

                var label = subCategory.FolkloreBook.ExtractText();
                if (string.IsNullOrWhiteSpace(label) && subCategory.Item.TryGetValue(out var bookItem))
                {
                    label = bookItem.Name.ExtractText();
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                if (!baseSpecials.TryGetValue(baseId, out var specials))
                {
                    baseSpecials[baseId] = specials = new HashSet<string>(StringComparer.Ordinal);
                }

                specials.Add(label);
            }

            // 只保留有常规采集点的物品（采集笔记/成就只统计这些），并去掉活动物品。
            var groups = new Dictionary<(uint ItemId, GatheringBranch Branch), GatheringEntryBuilder>();
            foreach (var pointBase in pointBases.Values)
            {
                if (!liveBases.Contains(pointBase.RowId))
                {
                    continue;
                }

                var branch = ClassifyGatheringType(pointBase.GatheringType.RowId);
                if (branch == GatheringBranch.None)
                {
                    continue;
                }

                var nodes = baseNodes.GetValueOrDefault(pointBase.RowId);
                var specials = baseSpecials.GetValueOrDefault(pointBase.RowId);
                for (var index = 0; index < pointBase.Item.Count; index++)
                {
                    var gatheringItemId = pointBase.Item[index].RowId;
                    if (gatheringItemId == 0 || gatheringItemId > ushort.MaxValue ||
                        !gatheringItems.TryGetValue(gatheringItemId, out var gatheringItem))
                    {
                        continue;
                    }

                    var itemId = gatheringItem.Item.RowId;
                    if (itemId == 0)
                    {
                        continue;
                    }

                    var key = (itemId, branch);
                    if (!groups.TryGetValue(key, out var builder))
                    {
                        if (!TryDescribeItem(gatheringItem, out var name, out var iconId, out var isCollectable) ||
                            string.IsNullOrWhiteSpace(name))
                        {
                            continue;
                        }

                        groups[key] = builder = new GatheringEntryBuilder
                        {
                            ItemId = itemId,
                            Name = name,
                            IconId = iconId,
                            IsCollectable = isCollectable
                        };
                    }

                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            if (!builder.Nodes.Any(existing => existing.Key == node.Key))
                            {
                                builder.Nodes.Add(node);
                            }
                        }
                    }

                    if (specials != null)
                    {
                        foreach (var special in specials)
                        {
                            builder.SpecialCategories.Add(special);
                        }
                    }

                    if (!builder.GatheringItemIds.Contains(gatheringItemId))
                    {
                        builder.GatheringItemIds.Add(gatheringItemId);
                    }

                    if (gatheringItemId > builder.RepresentativeGatheringItemId)
                    {
                        builder.RepresentativeGatheringItemId = gatheringItemId;
                    }
                }
            }

            foreach (var (key, builder) in groups)
            {
                byte level = 0;
                byte stars = 0;
                if (gatheringItems.TryGetValue(builder.RepresentativeGatheringItemId, out var representative) &&
                    representative.GatheringItemLevel.TryGetValue(out var itemLevel))
                {
                    level = itemLevel.GatheringItemLevel;
                    stars = itemLevel.Stars;
                }

                var entry = new GatheringEntry
                {
                    ItemId = builder.ItemId,
                    IconId = builder.IconId,
                    Name = builder.Name,
                    Level = level,
                    Stars = stars,
                    IsCollectable = builder.IsCollectable,
                    GatheringItemIds = builder.GatheringItemIds.ToArray(),
                    Nodes = builder.Nodes.ToArray(),
                    SpecialCategories = builder.SpecialCategories.ToArray()
                };

                if (key.Branch == GatheringBranch.Mining)
                {
                    result.Mining.Add(entry);
                }
                else
                {
                    result.Botany.Add(entry);
                }
            }

            result.Mining.Sort(CompareEntries);
            result.Botany.Sort(CompareEntries);
            return result;
        }

        internal static bool TryDescribeItem(
            GatheringItem gatheringItem,
            out string name,
            out uint iconId,
            out bool isCollectable)
        {
            isCollectable = false;
            if (gatheringItem.Item.TryGetValue<Item>(out var item))
            {
                name = item.Name.ExtractText();
                iconId = item.Icon;
                isCollectable = item.IsCollectable;
                return !string.IsNullOrWhiteSpace(name);
            }

            name = string.Empty;
            iconId = 0;
            return false;
        }
    }
}

internal sealed class GatheringEntry
{
    public uint ItemId { get; init; }
    public uint IconId { get; init; }
    public string Name { get; init; } = string.Empty;
    public byte Level { get; init; }
    public byte Stars { get; init; }
    public bool IsCollectable { get; init; }
    public IReadOnlyList<uint> GatheringItemIds { get; init; } = [];
    public IReadOnlyList<GatheringNodeInfo> Nodes { get; init; } = [];
    public IReadOnlyList<string> SpecialCategories { get; init; } = [];
    public bool Collected { get; set; }
    public bool Selected { get; set; }
}

internal sealed class GatheringNodeInfo
{
    public string Territory { get; init; } = string.Empty;
    public string Place { get; init; } = string.Empty;
    public byte Level { get; init; }
    public string Key => $"{Territory}|{Place}|{Level}";
}

internal sealed class GatheringCategory
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public byte MinLevel { get; init; }
    public byte MaxLevel { get; init; }
    public bool IsSpecial { get; init; }
    public bool CollectableOnly { get; init; }
    public string SpecialLabel { get; init; } = string.Empty;

    public bool Matches(GatheringEntry entry)
    {
        if (CollectableOnly)
        {
            return entry.IsCollectable;
        }

        if (SpecialLabel.Length > 0)
        {
            return entry.SpecialCategories.Contains(SpecialLabel, StringComparer.Ordinal);
        }

        return MaxLevel > 0 && entry.Level >= MinLevel && entry.Level <= MaxLevel;
    }
}

internal sealed class GatheringEntryBuilder
{
    public uint ItemId;
    public string Name = string.Empty;
    public uint IconId;
    public bool IsCollectable;
    public uint RepresentativeGatheringItemId;
    public List<uint> GatheringItemIds { get; } = [];
    public List<GatheringNodeInfo> Nodes { get; } = [];
    public HashSet<string> SpecialCategories { get; } = new(StringComparer.Ordinal);
}

internal sealed class GbrPreset(string name, string description, List<uint> itemIds, string base64)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<uint> ItemIds { get; } = itemIds;
    public string Base64 { get; } = base64;
}

internal sealed class RuriOrderJson
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; } = 1;
    public string SourceIntent { get; set; } = "Gather";
    public bool SkipIfEnough { get; set; }
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
