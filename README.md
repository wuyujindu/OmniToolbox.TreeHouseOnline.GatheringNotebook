# OmniToolbox.TreeHouseOnline.GatheringNotebook

Omni Toolbox 在线模块仓库：**采集笔记图鉴**（`GatheringNotebook`）。模块使用 **API 1**，需要 Omni **1.1.8.2 或更新版本**。

> 仓库地址：`https://github.com/wuyujindu/OmniToolbox.TreeHouseOnline.GatheringNotebook`（公开仓库）。
> 如果以后修改仓库名，需要同步修改 `TreeHouseModules.json` 的 `RepositoryURL` 和本文件里的安装地址。

## 安装与更新

在“插件设置 → 在线模块”中填写仓库地址：

```text
https://github.com/wuyujindu/OmniToolbox.TreeHouseOnline.GatheringNotebook
```

Omni 读取默认分支根目录的 `TreeHouseModules.json`，下载并校验模块。首次安装后默认关闭，在“树树妙妙屋 → 在线”中手动启用。

在线模块拥有与 Omni 相同的运行权限，请仅安装可信来源。更新时同时发布模块文件与清单，递增版本并重新计算 SHA256；只修改摘要不会触发已安装模块的版本更新。用户配置保留在宿主的 `TreeHouse` 存储中。

## 采集笔记图鉴

`GatheringNotebook` 读取角色采集笔记的完成情况，对应成就「专研采掘笔记」「专研采伐笔记」。界面严格按游戏内采集笔记的层级做成四栏：

- **左栏**：先选 `采矿 / 园艺`，再选具体种类 —— 采矿下有 **采掘 / 碎石**，园艺下有 **采伐 / 割草**。
  每个种类下分两块（都可折叠，右侧带「全选」）：
  - **采集等级**：1-5、6-10 … 96-100，显示每档未采集数；包含该种类下的全部物品。
  - **特殊分类**：固定三项 —— **收藏品交易**（`Item.IsCollectable`）、**传承录**（`GatheringSubCategory.Division >= 2000`）、**支线任务**（`Division == 0` 且带任务编号的采集点）。特殊分类是等级列表的子集，同一物品可以在两边同时出现（游戏内行为）。
- **中栏**：物品列表（勾选框 / 图标 / 名称 / 等级），文字居中，鼠标悬停即在右栏显示详情。
- **第 3 栏**：物品名称、等级、采集点列表（地图 · 地点 等级）。
- **最右栏**：已选择的物品（按等级排序，每条可点 × 取消选择）。

数据说明：

- **只统计计入采集笔记成就的物品**：必须有常规采集点（`GatheringPoint` 引用了它的 `GatheringPointBase`）；`EventItem` 活动物品、宇宙探索等没有常规采集点的条目全部不收录。
- 采集物来自 `GatheringItem`，按 `GatheringPointBase.GatheringType` 归入分支：`0/1`（采掘、碎石）算采矿，`2/3`（采伐、割草）算园艺，捕鱼用的类型不纳入。
- 完成状态调用 `QuestManager.IsGatheringItemGathered(gatheringItemId)`，参数是 `GatheringItem` 的 RowId；只在角色登录后读取，未登录时仅显示提示。
- **同一物品只列一条**：一个物品可能对应多个 `GatheringItem` 行（不同等级/节点），模块按物品 ID 合并，采集状态取「任意一行采集过」。
- 收藏品用 `Item.IsCollectable` 判定；特殊分类来自 `GatheringSubCategory.Division >= 2000` 的子分类名。
- 设置项：显示已采集、按名称搜索、手动刷新；设置界面打开时每 2 秒自动刷新一次。

### 导出 GatherBuddy Reborn 自动采集预设

列表每行都有勾选框，列表上方有「全选」「清除选择」（作用于当前等级/分类下的筛选结果）。选好未采集物后：

- **导出 GBR 预设**：按分支（采矿 / 园艺）生成预设，**每个预设最多 100 项**（超出自动拆成第 2、3… 组），
  **直接写入 GBR 的 `auto_gather_lists.json`**。GBR 的列表管理器监听这个文件，写完后会自动重新加载；
  同名预设会被覆盖，预设名形如 `采矿 未采集 1`，数量全部为 1、全部启用、不勾选 Fallback。
- **复制预设**：备用方式，弹窗里逐个「复制」，再到 GBR 自动采集列表点「从剪贴板导入」。

写入位置是 `%APPDATA%\XIVLauncherCN\pluginConfigs\GatherbuddyReborn\auto_gather_lists.json`（由 Omni 的配置目录推算，不写死盘符）。如果 GBR 没有立即刷新列表，重新打开一次它的窗口即可。写文件前会先读现有列表再合并，损坏的 JSON 会被当成空列表重建。

剪贴板方式的内容是 GBR 自己的格式：`Base64(GZip(0x06 + JSON))`，JSON 字段与 `auto_gather_lists.json` 一致（`ItemIds` / `Quantities` / `PrefferedLocations` / `EnabledItems` / `Name` / `Description` / `FolderPath` / `Order` / `Enabled` / `Fallback` / `RemoveCompletedItems`）。自测覆盖了文件合并（新增、同名覆盖、坏 JSON 重建）和 Base64 解码，并且已在 7.5.0.3 上用 GBR 的 `Config.FromBase64` 实测可以解析。

### 生成琉璃（Ruri）订单

选中未采集物后有两个按钮：

- **生成 Ruri 订单**：生成 Ruri 订单 JSON（每项 `ItemId` + `Quantity=1` + `SourceIntent="Gather"` + `SkipIfEnough=false`），复制到剪贴板，打开 Ruri 主窗口（`/ruri`），再执行 `/ruri dryrun <json>` 让 Ruri 做计划检查 —— 只出方案，不会开始采集。
- **Ruri 立即采集**：同样生成订单并打开 Ruri，但执行 `/ruri run <json>`，Ruri 会直接开始按订单采集。

Ruri 没有订单相关的 IPC，这里走的是它的命令接口（`/ruri`、`/ruri dryrun`、`/ruri run`）。生成的 JSON 已用 Ruri 自带的 `OrderImporter.ImportFromJson` 实测可以解析，`SourceIntent` 会正确识别为 `Gather`。需要 Ruri 插件已启用且许可证有效，否则命令会被拒绝（模块会在聊天框提示）。

当前国服数据下：采掘 352、碎石 227、采伐 285、割草 408（都计入采集笔记成就，全部可导出给 GBR）。由于完成状态来自游戏内存，只有在游戏内登录角色后才会显示真实进度。

## 模块组织与维护

```text
OmniToolbox.TreeHouseOnline.GatheringNotebook/
  Modules/
    GatheringNotebook.cs
  README.md
  TreeHouseModules.json
  Tools/Validation/
```

一个仓库可以包含多个模块，但本仓库只发布 `GatheringNotebook`，方便单独更新而不影响其他在线模块。每个发布的 `.cs` 或 `.dll` 只能有一个可实例化模块；源码模式按文件独立编译，辅助类型和词条放在模块文件内。

模块继承 `OmniModuleBase` 并声明 `[ModuleApiVersion(1)]`；事件在 `OnStart(FeatureLifetime activation)` 中接入，并通过 `activation.OnStopping` 登记解绑。不要打包宿主提供的 Common、TreeTools、TreeToolKit 等公共依赖。

## 编译与自测

需要 .NET 10 SDK，以及已安装的当前 Omni 和 Dalamud 依赖。从仓库根目录执行，替换两个宿主路径：

```powershell
$omniPath = '<Omni 插件版本目录>'
$dalamudPath = '<Dalamud addon\Hooks\dev 目录>'
dotnet build Tools/Validation/Validation.csproj -c Release -p:ModuleName=GatheringNotebook "-p:OmniPath=$omniPath" "-p:DalamudPath=$dalamudPath"
dotnet run --project Tools/Validation/Validation.csproj -c Release "-p:OmniPath=$omniPath" "-p:DalamudPath=$dalamudPath"
```

第一条命令按文件独立编译模块；第二条运行宿主 API 契约、简繁词库、清单元数据与 LF SHA256、采集笔记分支/筛选/排序/重置测试。测试程序集不加入发布清单。

本次使用 Omni 1.2.0.6 与当前 Dalamud dev 的真实程序集验证。游戏内的进度读取、列表绘制与宿主下载、重启后的配置恢复仍需在 Omni 中验收。

## 发布校验

最终模块文件的 SHA256 必须与清单一致，修改代码、编码、换行后均需重新计算。本仓库源码在 Git 中按 LF 存储（`.gitattributes` 已固定），清单摘要按 LF 内容计算：

```powershell
$path = "Modules/GatheringNotebook.cs"
$text = [System.Text.Encoding]::UTF8.GetString([System.IO.File]::ReadAllBytes((Resolve-Path $path)))
$bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($text.Replace("`r`n", "`n"))
-join ([System.Security.Cryptography.SHA256]::Create().ComputeHash($bytes) | ForEach-Object { $_.ToString("X2") })
```

清单中的 `ApiVersion` 为 `1`，`MinimumOmniVersion` 为 `1.1.8.2`，`RepositoryURL` 必须与本仓库一致，`Localization.Source` 提供非空的 `Title`、`Description`。

上传后在 Omni 中刷新仓库，验证下载、加载及配置保存。Git 可能转换换行符，远端校验应以清单指向的原始文件字节为准。
