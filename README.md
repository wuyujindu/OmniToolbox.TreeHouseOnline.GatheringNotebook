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

`GatheringNotebook` 读取角色采集笔记的完成情况，按**采矿**和**园艺**两个分支列出尚未采集过的采集物。列表显示图标、名称、等级与星级，并给出两个分支的已采集进度。

- 采集物来自 `GatheringItem`，按 `GatheringPointBase.GatheringType` 归入分支：`0/1`（采掘、碎石）算采矿，`2/3`（采伐、割草）算园艺，捕鱼用的类型不纳入。
- 完成状态调用 `QuestManager.IsGatheringItemGathered(gatheringItemId)`，参数是 `GatheringItem` 的 RowId；只在角色登录后读取，未登录时仅显示提示。
- 采集物名称与图标同时兼容 `Item` 与 `EventItem`（活动采集物），活动物品取 `EventItem.Singular`。`GatheringItem.IsHidden` 标记的条目也会列出，因此总数可能比游戏内列表略多。
- 设置项：显示已采集、按名称搜索、手动刷新；设置界面打开时每 2 秒自动刷新一次。

### 导出 GatherBuddy Reborn 自动采集预设

列表每行都有勾选框，分支上方有「全选」「清除选择」（作用于当前筛选结果）。选好未采集物后点「导出 GBR 预设」：

- 按分支（采矿 / 园艺）生成预设，**每个预设最多 100 项**，超出自动拆成第 2、3… 组；
- 弹窗里逐个点「复制」，然后在 GatherBuddy Reborn 的自动采集列表点「从剪贴板导入」，粘贴即可；
- 预设名形如 `采矿 未采集 1`，数量全部为 1、全部启用、不勾选 Fallback；
- `EventItem` 活动采集物会被跳过（GBR 里没有对应采集物），弹窗会显示跳过了几个。

导出的剪贴板内容是 GBR 自己的格式：`Base64(GZip(0x06 + JSON))`，其中 JSON 字段与 `auto_gather_lists.json` 一致（`ItemIds` / `Quantities` / `PrefferedLocations` / `EnabledItems` / `Name` / `Description` / `FolderPath` / `Order` / `Enabled` / `Fallback` / `RemoveCompletedItems`）。自测会用同样的解码流程校验，并且已在 7.5.0.3 上用 GBR 的 `Config.FromBase64` 实测可以解析。

当前国服数据下两个分支约 980 / 1100 条。由于完成状态来自游戏内存，只有在游戏内登录角色后才会显示真实进度。

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
