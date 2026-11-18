using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            ["TypeMining"] = "采掘",
            ["TypeQuarrying"] = "碎石",
            ["TypeLogging"] = "采伐",
            ["TypeHarvesting"] = "割草",
            ["SpecialCollectable"] = "收藏品交易",
            ["SpecialFolklore"] = "传承录",
            ["SpecialSideQuest"] = "支线任务",
            ["MiningProgress"] = "采矿 已采集 {0} / {1}",
            ["BotanyProgress"] = "园艺 已采集 {0} / {1}",
            ["LevelSection"] = "采集等级",
            ["SpecialSection"] = "特殊分类",
            ["SelectedSection"] = "已选择",
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
            ["ExportPresetsHelp"] = "把选中物品按每 100 项生成预设并直接写入 GBR 的自动采集列表 (同名预设会被覆盖), GBR 会自动重新加载",
            ["CopyPresets"] = "复制预设",
            ["CopyPresetsHelp"] = "逐个复制预设, 到 GBR 里用「从剪贴板导入」(备用方式)",
            ["GbrWritten"] = "已写入 {0} 个预设到 GBR",
            ["GbrWriteFailed"] = "写入 GBR 列表失败: {0}",
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
    private int activeJob;
    private int activeType;
    private bool levelSectionOpen = true;
    private bool specialSectionOpen = true;
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
                ExportPresetsToGbr();
            }
        }

        OmniControls.HelpTooltip(Text("ExportPresetsHelp"));

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        using (ImRaii.Disabled(selected == 0))
        {
            if (OmniControls.SmallButton(Text("CopyPresets") + "##gatheringNotebookCopyPresets", false))
            {
                BuildPresets();
                copiedPreset = string.Empty;
                ImGui.OpenPopup("##gatheringNotebookPresets");
            }
        }

        OmniControls.HelpTooltip(Text("CopyPresetsHelp"));

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

        var entries = catalog.All.ToList();
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

        return catalog.All.Count(entry => entry.Selected);
    }

    private void DrawProgress()
    {
        if (catalog == null)
        {
            return;
        }

        var miningEntries = catalog.ForJob(0).ToList();
        var botanyEntries = catalog.ForJob(1).ToList();
        ImGui.TextUnformatted(Text("MiningProgress", miningEntries.Count(entry => entry.Collected), miningEntries.Count));
        ImGui.SameLine(0f, OmniTheme.Scale(18f));
        ImGui.TextUnformatted(Text("BotanyProgress", botanyEntries.Count(entry => entry.Collected), botanyEntries.Count));
    }

    private void DrawBody()
    {
        if (catalog == null)
        {
            return;
        }

        var entries = catalog.Groups[activeType];
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
        var spacing = OmniTheme.Scale(8f);
        var available = ImGui.GetContentRegionAvail().X;
        var sidebarWidth = OmniTheme.Scale(190f);
        var detailWidth = OmniTheme.Scale(180f);
        var selectedWidth = OmniTheme.Scale(195f);
        var listWidth = MathF.Max(
            OmniTheme.Scale(220f),
            available - sidebarWidth - detailWidth - selectedWidth - spacing * 3f);

        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(OmniTheme.Scale(10f), OmniTheme.Scale(8f))))
        {
            using (var sidebar = ImRaii.Child("##gatheringNotebookSidebar", new Vector2(sidebarWidth, height), true))
            {
                if (sidebar)
                {
                    DrawSidebar(entries, categories);
                }
            }

            ImGui.SameLine(0f, spacing);
            using (var list = ImRaii.Child("##gatheringNotebookList", new Vector2(listWidth, height), true))
            {
                if (list)
                {
                    DrawList(entries, category);
                }
            }

            ImGui.SameLine(0f, spacing);
            using (var detail = ImRaii.Child("##gatheringNotebookDetail", new Vector2(detailWidth, height), true))
            {
                if (detail)
                {
                    DrawDetail();
                }
            }

            ImGui.SameLine(0f, spacing);
            using (var selected = ImRaii.Child("##gatheringNotebookSelected", new Vector2(selectedWidth, height), true))
            {
                if (selected)
                {
                    DrawSelected();
                }
            }
        }
    }

    private void DrawSidebar(List<GatheringEntry> entries, List<GatheringCategory> categories)
    {
        var jobWidth = (ImGui.GetContentRegionAvail().X - OmniTheme.Scale(6f)) * 0.5f;
        if (OmniControls.SmallButton(Text("Mining") + "##gatheringNotebookJobMining", activeJob == 0, new Vector2(jobWidth, 0f)))
        {
            SwitchJob(0);
        }

        ImGui.SameLine(0f, OmniTheme.Scale(6f));
        if (OmniControls.SmallButton(Text("Botany") + "##gatheringNotebookJobBotany", activeJob == 1, new Vector2(jobWidth, 0f)))
        {
            SwitchJob(1);
        }

        var typeKeys = activeJob == 0 ? new[] { "TypeMining", "TypeQuarrying" } : new[] { "TypeLogging", "TypeHarvesting" };
        var typeWidth = (ImGui.GetContentRegionAvail().X - OmniTheme.Scale(6f)) * 0.5f;
        for (var index = 0; index < typeKeys.Length; index++)
        {
            var type = activeJob * 2 + index;
            if (index > 0)
            {
                ImGui.SameLine(0f, OmniTheme.Scale(6f));
            }

            if (OmniControls.SmallButton(Text(typeKeys[index]) + "##gatheringNotebookType" + type, activeType == type, new Vector2(typeWidth, 0f)))
            {
                SwitchType(type);
            }
        }

        ImGui.Spacing();
        if (DrawSectionHeader("LevelSection", "##gatheringNotebookLevels", entries, false, ref levelSectionOpen))
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
        if (DrawSectionHeader("SpecialSection", "##gatheringNotebookSpecial", entries, true, ref specialSectionOpen))
        {
            foreach (var category in categories.Where(item => item.IsSpecial))
            {
                DrawCategoryRow(entries, category);
            }
        }
    }

    private bool DrawSectionHeader(string key, string id, List<GatheringEntry> entries, bool special, ref bool open)
    {
        var candidates = entries
            .Where(entry => IsSpecialEntry(entry) == special)
            .Where(entry => config.ShowCollected || !entry.Collected)
            .Where(entry => search.Length == 0 ||
                            entry.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
        var allSelected = candidates.Count > 0 && candidates.All(entry => entry.Selected);

        var label = Text(key);
        var startPosition = ImGui.GetCursorScreenPos();
        var contentWidth = ImGui.GetContentRegionAvail().X;
        var arrowWidth = OmniTheme.Scale(9f);
        var spacing = OmniTheme.Scale(7f);
        var checkboxSize = OmniTheme.Scale(14f);
        const float titleScale = 1.45f;

        ImGui.SetWindowFontScale(1f);
        var textWidth = ImGui.CalcTextSize(label).X * titleScale;
        var textHeight = ImGui.GetTextLineHeight() * titleScale;
        var rowHeight = textHeight + OmniTheme.Scale(8f);

        var groupWidth = arrowWidth + spacing + textWidth + spacing + checkboxSize;
        var startX = startPosition.X + MathF.Max(0f, (contentWidth - groupWidth) * 0.5f);
        var centerY = startPosition.Y + rowHeight * 0.5f;

        ImGui.SetCursorScreenPos(new Vector2(startX, startPosition.Y));
        ImGui.InvisibleButton(id + "##sectionHeader", new Vector2(arrowWidth + spacing + textWidth, rowHeight));
        if (ImGui.IsItemClicked())
        {
            open = !open;
        }

        var drawList = ImGui.GetWindowDrawList();
        var accent = ImGui.ColorConvertFloat4ToU32(OmniTheme.ControlAccent);
        var arrowTop = centerY - OmniTheme.Scale(5f);
        var arrowBottom = centerY + OmniTheme.Scale(5f);
        if (open)
        {
            drawList.AddTriangleFilled(
                new Vector2(startX, arrowTop),
                new Vector2(startX + arrowWidth, centerY),
                new Vector2(startX, arrowBottom),
                accent);
        }
        else
        {
            drawList.AddTriangleFilled(
                new Vector2(startX, arrowTop),
                new Vector2(startX, arrowBottom),
                new Vector2(startX + arrowWidth, centerY),
                accent);
        }

        ImGui.SetWindowFontScale(titleScale);
        ImGui.SetCursorScreenPos(new Vector2(startX + arrowWidth + spacing, centerY - textHeight * 0.5f));
        ImGui.TextColored(OmniTheme.ControlAccent, label);
        ImGui.SetWindowFontScale(1f);

        ImGui.SetCursorScreenPos(new Vector2(
            startX + arrowWidth + spacing + textWidth + spacing,
            centerY - checkboxSize * 0.5f));
        using (ImRaii.Disabled(candidates.Count == 0))
        {
            if (DrawSmallCheckbox("##selectSection" + id, allSelected, checkboxSize))
            {
                SelectSection(entries, special, !allSelected);
            }
        }

        if (ImGui.IsItemHovered())
        {
            OmniControls.HelpTooltip(Text("SelectAll"), true);
        }

        ImGui.SetCursorScreenPos(new Vector2(startPosition.X, startPosition.Y + rowHeight));
        return open;
    }

    private void SelectSection(List<GatheringEntry> entries, bool special, bool select)
    {
        foreach (var entry in entries)
        {
            if (IsSpecialEntry(entry) != special)
            {
                continue;
            }

            if (!config.ShowCollected && entry.Collected)
            {
                continue;
            }

            if (search.Length > 0 && entry.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            entry.Selected = select;
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

    private void DrawSelected()
    {
        if (catalog == null)
        {
            return;
        }

        var selected = catalog.All
            .Where(entry => entry.Selected)
            .GroupBy(entry => entry.ItemId)
            .Select(group => group.First())
            .OrderBy(entry => entry.Level)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ToList();

        DrawCenteredText($"{Text("SelectedSection")} ({selected.Count})");
        using (ImRaii.Disabled(selected.Count == 0))
        {
            if (OmniControls.SmallButton(Text("ClearSelection") + "##gatheringNotebookClearSelected", false))
            {
                ClearAllSelection();
            }
        }

        ImGui.Separator();

        if (selected.Count == 0)
        {
            ImGui.TextUnformatted(Text("NoSelection"));
            return;
        }

        foreach (var entry in selected)
        {
            ImGui.PushID((int)entry.ItemId);
            var remove = OmniControls.SmallButton("×##removeSelected", false);
            ImGui.SameLine(0f, OmniTheme.Scale(6f));
            ImGui.TextUnformatted($"{entry.Name} ({entry.Level})");
            if (remove)
            {
                ClearSelection(entry.ItemId);
            }

            ImGui.PopID();
        }
    }

    private void ClearAllSelection()
    {
        if (catalog == null)
        {
            return;
        }

        foreach (var entry in catalog.All)
        {
            entry.Selected = false;
        }
    }

    private void ClearSelection(uint itemId)
    {
        if (catalog == null)
        {
            return;
        }

        foreach (var entry in catalog.All)
        {
            if (entry.ItemId == itemId)
            {
                entry.Selected = false;
            }
        }
    }

    private static bool DrawSmallCheckbox(string id, bool value, float size)
    {
        var position = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(size, size));
        var hovered = ImGui.IsItemHovered();
        var drawList = ImGui.GetWindowDrawList();
        var rounding = size * 0.28f;
        var accent = OmniTheme.ControlAccent;

        if (value)
        {
            drawList.AddRectFilled(position, position + new Vector2(size, size), ImGui.ColorConvertFloat4ToU32(accent), rounding);
            var thickness = MathF.Max(1.2f, size * 0.14f);
            drawList.AddLine(
                position + new Vector2(size * 0.24f, size * 0.52f),
                position + new Vector2(size * 0.43f, size * 0.72f),
                0xFFFFFFFF,
                thickness);
            drawList.AddLine(
                position + new Vector2(size * 0.43f, size * 0.72f),
                position + new Vector2(size * 0.78f, size * 0.30f),
                0xFFFFFFFF,
                thickness);
        }
        else
        {
            var frame = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
            drawList.AddRectFilled(
                position,
                position + new Vector2(size, size),
                ImGui.ColorConvertFloat4ToU32(new Vector4(frame.X, frame.Y, frame.Z, 0.55f)),
                rounding);
            var border = hovered ? accent : new Vector4(0.55f, 0.58f, 0.5f, 0.8f);
            drawList.AddRect(
                position,
                position + new Vector2(size, size),
                ImGui.ColorConvertFloat4ToU32(border),
                rounding,
                ImDrawFlags.None,
                MathF.Max(1f, size * 0.08f));
        }

        return clicked;
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
            if (entries.All(entry => entry.HasSubCategory || entry.Level < min || entry.Level > max))
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

        var specials = new[]
        {
            (GatheringCategoryKind.Collectable, "SpecialCollectable"),
            (GatheringCategoryKind.Folklore, "SpecialFolklore"),
            (GatheringCategoryKind.SideQuest, "SpecialSideQuest")
        };
        foreach (var (kind, key) in specials)
        {
            result.Add(new GatheringCategory
            {
                Key = "special:" + kind,
                Label = Text(key),
                IsSpecial = true,
                Kind = kind
            });
        }

        return result;
    }

    internal static bool IsSpecialEntry(GatheringEntry entry) => entry.HasSubCategory;

    private void SwitchJob(int job)
    {
        if (activeJob == job)
        {
            return;
        }

        activeJob = job;
        activeType = job * 2;
        selectedCategoryKey = string.Empty;
        detailEntry = null;
    }

    private void SwitchType(int type)
    {
        if (activeType == type)
        {
            return;
        }

        activeType = type;
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
            catalog.ForJob(0).ToList(),
            catalog.ForJob(1).ToList(),
            Text("Mining"),
            Text("Botany"),
            Text("PresetName"),
            Text("PresetDescription")));
    }

    private void ExportPresetsToGbr()
    {
        BuildPresets();
        if (presets.Count == 0)
        {
            DalamudServices.ChatGUI.Print(Text("NoSelection"));
            return;
        }

        var path = ResolveGbrListPath();
        if (path == null)
        {
            DalamudServices.ChatGUI.Print(Text("GbrWriteFailed", "找不到 GBR 配置目录"));
            return;
        }

        try
        {
            var existing = File.Exists(path) ? File.ReadAllText(path) : "[]";
            if (string.IsNullOrWhiteSpace(existing))
            {
                existing = "[]";
            }

            var json = MergeGbrLists(existing, presets);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, true);
            DalamudServices.ChatGUI.Print(Text("GbrWritten", presets.Count));
        }
        catch (Exception exception)
        {
            DalamudServices.ChatGUI.Print(Text("GbrWriteFailed", exception.Message));
        }
    }

    internal static string MergeGbrLists(string existingJson, IEnumerable<GbrPreset> presets)
    {
        var array = new JsonArray();
        try
        {
            if (JsonNode.Parse(existingJson) is JsonArray parsed)
            {
                array = parsed;
            }
        }
        catch (JsonException)
        {
            array = new JsonArray();
        }

        var order = 0;
        foreach (var node in array)
        {
            if (node?["Order"] is JsonValue value && value.TryGetValue<int>(out var existingOrder))
            {
                order = Math.Max(order, existingOrder);
            }
        }

        foreach (var preset in presets)
        {
            for (var index = array.Count - 1; index >= 0; index--)
            {
                if (array[index]?["Name"] is JsonValue nameValue &&
                    nameValue.TryGetValue<string>(out var existingName) &&
                    string.Equals(existingName, preset.Name, StringComparison.Ordinal))
                {
                    array.RemoveAt(index);
                }
            }

            order++;
            var payload = new GbrPresetPayload
            {
                Name = preset.Name,
                Description = preset.Description,
                Order = order,
                ItemIds = preset.ItemIds.ToArray()
            };

            foreach (var itemId in preset.ItemIds)
            {
                payload.Quantities[itemId] = 1;
                payload.EnabledItems[itemId] = true;
            }

            var node = JsonSerializer.SerializeToNode(payload)!.AsObject();
            // GBR 的列表文件里没有 CurrentVersion, 保持和游戏内保存的格式一致。
            node.Remove("CurrentVersion");
            array.Add(node);
        }

        return array.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

    private static string? ResolveGbrListPath()
    {
        try
        {
            var directory = DalamudServices.PluginInterface.GetPluginConfigDirectory();
            var parent = Directory.GetParent(directory)?.FullName;
            return parent == null ? null : Path.Combine(parent, "GatherbuddyReborn", "auto_gather_lists.json");
        }
        catch
        {
            return null;
        }
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
        foreach (var entry in catalog.All)
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

    internal static bool IsGatheringJobType(uint gatheringTypeId) => gatheringTypeId <= 3;

    internal static int JobOfGatheringType(uint gatheringTypeId) => gatheringTypeId <= 1 ? 0 : 1;

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
        // 索引 = GatheringType: 0 采掘, 1 碎石, 2 采伐, 3 割草
        public List<GatheringEntry>[] Groups { get; } = [new(), new(), new(), new()];

        public List<GatheringEntry> ForType(int type) => Groups[type];

        public IEnumerable<GatheringEntry> ForJob(int job) => Groups[job * 2].Concat(Groups[job * 2 + 1]);

        public IEnumerable<GatheringEntry> All => Groups.SelectMany(group => group);

        private static bool IsFolkloreSubCategory(GatheringSubCategory subCategory)
        {
            if (subCategory.Division < 2000)
            {
                return false;
            }

            var label = subCategory.FolkloreBook.ExtractText();
            if (string.IsNullOrWhiteSpace(label) && subCategory.Item.TryGetValue(out var item))
            {
                label = item.Name.ExtractText();
            }

            return label.Contains("传承录", StringComparison.Ordinal);
        }

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

            var points = new Dictionary<uint, GatheringPoint>();
            foreach (var row in LuminaGetter.Get<GatheringPoint>())
            {
                points[row.RowId] = row;
            }

            var subCategories = new Dictionary<uint, GatheringSubCategory>();
            foreach (var row in LuminaGetter.Get<GatheringSubCategory>())
            {
                subCategories[row.RowId] = row;
            }

            // GatheringItemPoint：行 = GatheringItem，子行 = 实际放置它的采集点。
            // 游戏内采集笔记只统计这张表里的物品，采集等级列表也以它为准。
            var described = new Dictionary<uint, (bool Ok, string Name, uint Icon, bool Collectable, byte Level, byte Stars)>();
            var groups = new Dictionary<(uint ItemId, int TypeId), GatheringEntryBuilder>();
            foreach (var collection in LuminaGetter.GetSub<GatheringItemPoint>())
            {
                foreach (var subRow in collection)
                {
                    var gatheringItemId = subRow.RowId;
                    if (!described.TryGetValue(gatheringItemId, out var info))
                    {
                        info = (false, string.Empty, 0, false, 0, 0);
                        if (gatheringItems.TryGetValue(gatheringItemId, out var gatheringItem) &&
                            gatheringItem.Item.RowId != 0 &&
                            TryDescribeItem(gatheringItem, out var name, out var iconId, out var isCollectable) &&
                            !string.IsNullOrWhiteSpace(name))
                        {
                            byte level = 0;
                            byte stars = 0;
                            if (gatheringItem.GatheringItemLevel.TryGetValue(out var itemLevel))
                            {
                                level = itemLevel.GatheringItemLevel;
                                stars = itemLevel.Stars;
                            }

                            info = (true, name, iconId, isCollectable, level, stars);
                        }

                        described[gatheringItemId] = info;
                    }

                    if (!info.Ok ||
                        !points.TryGetValue(subRow.GatheringPoint.RowId, out var point) ||
                        !pointBases.TryGetValue(point.GatheringPointBase.RowId, out var pointBase) ||
                        !IsGatheringJobType(pointBase.GatheringType.RowId))
                    {
                        continue;
                    }

                    var itemId = gatheringItems[gatheringItemId].Item.RowId;
                    var typeId = (int)pointBase.GatheringType.RowId;
                    var key = (itemId, typeId);
                    if (!groups.TryGetValue(key, out var builder))
                    {
                        groups[key] = builder = new GatheringEntryBuilder
                        {
                            ItemId = itemId,
                            Name = info.Name,
                            IconId = info.Icon,
                            IsCollectable = info.Collectable,
                            Level = info.Level,
                            Stars = info.Stars
                        };
                    }

                    var subCategoryId = point.GatheringSubCategory.RowId;
                    GatheringSubCategory subCategory = default;
                    if (subCategoryId != 0 && subCategories.TryGetValue(subCategoryId, out subCategory))
                    {
                        builder.HasSubCategory = true;
                        if (subCategory.Division == 0 && subCategory.Quest.RowId != 0)
                        {
                            builder.InSideQuest = true;
                        }
                        else if (IsFolkloreSubCategory(subCategory))
                        {
                            builder.InFolklore = true;
                        }
                    }

                    if (!builder.GatheringItemIds.Contains(gatheringItemId))
                    {
                        builder.GatheringItemIds.Add(gatheringItemId);
                    }

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
                    if (territory.Length == 0 && place.Length == 0)
                    {
                        continue;
                    }

                    var node = new GatheringNodeInfo
                    {
                        Territory = territory,
                        Place = place,
                        Level = pointBase.GatheringLevel
                    };

                    if (!builder.Nodes.Any(existing => existing.Key == node.Key))
                    {
                        builder.Nodes.Add(node);
                    }
                }
            }

            // 支线任务：任务专用采集点（territory = 1）不在 GatheringItemPoint 里，单独补进来。
            var knownItems = new HashSet<uint>(groups.Keys.Select(key => key.ItemId));
            foreach (var point in points.Values)
            {
                var subCategoryId = point.GatheringSubCategory.RowId;
                GatheringSubCategory subCategory = default;
                if (subCategoryId == 0 || !subCategories.TryGetValue(subCategoryId, out subCategory) ||
                    subCategory.Division != 0 || subCategory.Quest.RowId == 0)
                {
                    continue;
                }

                if (!pointBases.TryGetValue(point.GatheringPointBase.RowId, out var pointBase) ||
                    !IsGatheringJobType(pointBase.GatheringType.RowId))
                {
                    continue;
                }

                var typeId = (int)pointBase.GatheringType.RowId;
                for (var index = 0; index < pointBase.Item.Count; index++)
                {
                    var gatheringItemId = pointBase.Item[index].RowId;
                    if (gatheringItemId == 0 || !gatheringItems.TryGetValue(gatheringItemId, out var gatheringItem))
                    {
                        continue;
                    }

                    var itemId = gatheringItem.Item.RowId;
                    if (itemId == 0 || knownItems.Contains(itemId) ||
                        !TryDescribeItem(gatheringItem, out var name, out var iconId, out var isCollectable) ||
                        string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var key = (itemId, typeId);
                    if (!groups.TryGetValue(key, out var builder))
                    {
                        byte level = 0;
                        byte stars = 0;
                        if (gatheringItem.GatheringItemLevel.TryGetValue(out var itemLevel))
                        {
                            level = itemLevel.GatheringItemLevel;
                            stars = itemLevel.Stars;
                        }

                        groups[key] = builder = new GatheringEntryBuilder
                        {
                            ItemId = itemId,
                            Name = name,
                            IconId = iconId,
                            IsCollectable = isCollectable,
                            Level = level,
                            Stars = stars
                        };
                        knownItems.Add(itemId);
                    }

                    builder.HasSubCategory = true;
                    builder.InSideQuest = true;
                    if (!builder.GatheringItemIds.Contains(gatheringItemId))
                    {
                        builder.GatheringItemIds.Add(gatheringItemId);
                    }
                }
            }

            foreach (var (key, builder) in groups)
            {
                var entry = new GatheringEntry
                {
                    ItemId = builder.ItemId,
                    IconId = builder.IconId,
                    Name = builder.Name,
                    Level = builder.Level,
                    Stars = builder.Stars,
                    IsCollectable = builder.IsCollectable,
                    HasSubCategory = builder.HasSubCategory,
                    InFolklore = builder.InFolklore,
                    InSideQuest = builder.InSideQuest,
                    GatheringItemIds = builder.GatheringItemIds.ToArray(),
                    Nodes = builder.Nodes.ToArray()
                };

                result.Groups[key.TypeId].Add(entry);
            }

            foreach (var group in result.Groups)
            {
                group.Sort(CompareEntries);
            }

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
    public bool HasSubCategory { get; init; }
    public bool InFolklore { get; init; }
    public bool InSideQuest { get; init; }
    public bool InCollectableTrade => IsCollectable && HasSubCategory && !InFolklore && !InSideQuest;
    public IReadOnlyList<uint> GatheringItemIds { get; init; } = [];
    public IReadOnlyList<GatheringNodeInfo> Nodes { get; init; } = [];
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

internal enum GatheringCategoryKind
{
    LevelRange,
    Collectable,
    Folklore,
    SideQuest
}

internal sealed class GatheringCategory
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public byte MinLevel { get; init; }
    public byte MaxLevel { get; init; }
    public bool IsSpecial { get; init; }
    public GatheringCategoryKind Kind { get; init; }

    public bool Matches(GatheringEntry entry) => Kind switch
    {
        GatheringCategoryKind.Collectable => entry.InCollectableTrade,
        GatheringCategoryKind.Folklore => entry.InFolklore,
        GatheringCategoryKind.SideQuest => entry.InSideQuest,
        _ => !entry.HasSubCategory && entry.Level >= MinLevel && entry.Level <= MaxLevel
    };
}

internal sealed class GatheringEntryBuilder
{
    public uint ItemId;
    public string Name = string.Empty;
    public uint IconId;
    public bool IsCollectable;
    public bool HasSubCategory;
    public bool InFolklore;
    public bool InSideQuest;
    public byte Level;
    public byte Stars;
    public List<uint> GatheringItemIds { get; } = [];
    public List<GatheringNodeInfo> Nodes { get; } = [];
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
