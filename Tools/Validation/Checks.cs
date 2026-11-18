using System.Reflection;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Security.Cryptography;
using OmniToolbox.Common.Module.Abstractions;
using OmniToolbox.Config;
using OmniToolbox.Host;
using OmniToolbox.TreeHouseOnline;
using OmniToolbox.UI;
using TreeTools.Lifecycle;

internal static class Checks
{
    private static int passed;

    public static Task Run()
    {
        var module = new GatheringNotebook();
        var register = typeof(OmniLoc).GetMethod("RegisterModule", BindingFlags.Static | BindingFlags.NonPublic)!;
        register.Invoke(null, [module]);
        typeof(OmniLoc).GetMethod("ApplyLanguage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [UILanguage.SimplifiedChinese]);
        var contract = typeof(OmniModuleBase).Assembly.GetType("OmniToolbox.Common.Module.Runtime.ModuleContract", true)!;
        var validate = contract.GetMethod("ValidateType", BindingFlags.Static | BindingFlags.NonPublic)!;

        Check("host API 1 contract", () =>
        {
            validate.Invoke(null, [typeof(GatheringNotebook)]);
            Expect(typeof(GatheringNotebook).GetCustomAttribute<ModuleApiVersionAttribute>()?.Version == 1);
        });
        Check("complete Chinese catalog", () =>
        {
            var source = module.Localization.Catalogs[UILanguage.SimplifiedChinese];
            var traditional = module.Localization.Catalogs[UILanguage.TraditionalChinese];
            Expect(source.Count == traditional.Count && source.Count >= 6);
            foreach (var key in source.Keys)
            {
                Expect(!string.IsNullOrWhiteSpace(OmniLoc.Get(module, key, UILanguage.SimplifiedChinese)));
                Expect(!string.IsNullOrWhiteSpace(OmniLoc.Get(module, key, UILanguage.TraditionalChinese)));
                _ = string.Format(source[key], 1, 2, 3);
            }

            Expect(source.ContainsKey("Title") && source.ContainsKey("Description"));
        });
        Check("manifest schema, API, metadata and Git LF SHA256", () =>
        {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes("TreeHouseModules.json"));
            Expect(manifest.RootElement.GetProperty("SchemaVersion").GetInt32() == 1);
            var items = manifest.RootElement.GetProperty("Modules").EnumerateArray().ToList();
            Expect(items.Count == 1);
            var item = items[0];
            Expect(item.GetProperty("InternalName").GetString() == nameof(GatheringNotebook));
            Expect(item.GetProperty("ApiVersion").GetInt32() == 1);
            Expect(Version.Parse(item.GetProperty("MinimumOmniVersion").GetString()!) >= new Version(1, 1, 8, 2));
            Expect(Version.TryParse(item.GetProperty("Version").GetString(), out var version) && version >= new Version(1, 0, 0));
            var uri = new Uri(item.GetProperty("RepositoryURL").GetString()!);
            Expect(uri.Scheme == "https" && uri.Host == "github.com" &&
                   uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2);
            Expect(!string.IsNullOrWhiteSpace(item.GetProperty("Author").GetString()));
            var file = item.GetProperty("File").GetString()!;
            Expect(File.Exists(file));
            // Git stores this source as LF; Windows working copies may be CRLF.
            var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(file).Replace("\r\n", "\n"));
            Expect(Convert.ToHexString(SHA256.HashData(bytes)) == item.GetProperty("Sha256").GetString());
            var source = module.Localization.Catalogs[UILanguage.SimplifiedChinese];
            var localization = item.GetProperty("Localization").GetProperty("Source");
            Expect(localization.GetProperty("Title").GetString() == source["Title"]);
            Expect(localization.GetProperty("Description").GetString() == source["Description"]);
        });
        Check("gathering notebook branches, filtering and sorting", () =>
        {
            Expect(GatheringNotebook.ClassifyGatheringType(0) == GatheringBranch.Mining);
            Expect(GatheringNotebook.ClassifyGatheringType(1) == GatheringBranch.Mining);
            Expect(GatheringNotebook.ClassifyGatheringType(2) == GatheringBranch.Botany);
            Expect(GatheringNotebook.ClassifyGatheringType(3) == GatheringBranch.Botany);
            Expect(GatheringNotebook.ClassifyGatheringType(4) == GatheringBranch.None);
            Expect(GatheringNotebook.ClassifyGatheringType(99) == GatheringBranch.None);

            var entries = new[]
            {
                new GatheringEntry { Name = "铁矿", Level = 10 },
                new GatheringEntry { Name = "秘银矿", Level = 30, Collected = true },
                new GatheringEntry { Name = "枫木原木", Level = 10 }
            };

            Expect(GatheringNotebook.Filter(entries, false, string.Empty, null).Count == 2);
            Expect(GatheringNotebook.Filter(entries, true, string.Empty, null).Count == 3);
            Expect(GatheringNotebook.Filter(entries, false, "矿", null).Count == 1);
            Expect(GatheringNotebook.Filter(entries, false, "  矿  ", null).Count == 1);
            Expect(GatheringNotebook.Filter(entries, false, "不存在", null).Count == 0);

            var levelCategory = new GatheringCategory { Key = "level:10-14", MinLevel = 10, MaxLevel = 14 };
            var collectableCategory = new GatheringCategory { Key = "collectable", IsSpecial = true, CollectableOnly = true };
            var folkloreCategory = new GatheringCategory { Key = "special:x", IsSpecial = true, SpecialLabel = "矿物传承录·龙堡篇" };
            var categorized = new[]
            {
                new GatheringEntry { Name = "低级矿", Level = 10 },
                new GatheringEntry { Name = "高级矿", Level = 20 },
                new GatheringEntry { Name = "收藏矿", Level = 12, IsCollectable = true },
                new GatheringEntry { Name = "传承矿", Level = 12, SpecialCategories = ["矿物传承录·龙堡篇"] }
            };
            Expect(GatheringNotebook.Filter(categorized, true, string.Empty, levelCategory).Count == 3);
            Expect(GatheringNotebook.Filter(categorized, true, string.Empty, collectableCategory).Count == 1);
            Expect(GatheringNotebook.Filter(categorized, true, string.Empty, folkloreCategory).Count == 1);
            Expect(GatheringNotebook.Filter(categorized, true, "矿", levelCategory).Count == 3);

            var sorted = entries.ToList();
            sorted.Sort(GatheringNotebook.CompareEntries);
            Expect(sorted[0].Level == 10 && sorted[1].Level == 10 && sorted[^1].Level == 30);

            var settings = (GatheringNotebook.Settings)typeof(GatheringNotebook)
                .GetField("config", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(module)!;
            Expect(!module.ResetSettings());
            settings.ShowCollected = true;
            Expect(module.ResetSettings() && !settings.ShowCollected);
            Expect(!module.ResetSettings());
        });

        Check("GBR preset export encoding, dedupe and chunking", () =>
        {
            static GatheringEntry Entry(uint itemId, bool selected) => new()
            {
                ItemId = itemId,
                Name = "item" + itemId,
                Level = 1,
                Selected = selected
            };

            var mining = Enumerable.Range(0, 250).Select(index => Entry((uint)(1000 + index), true)).ToList();
            var botany = Enumerable.Range(0, 120).Select(index => Entry((uint)(2000 + index), true)).ToList();
            botany.Add(Entry(1000, true)); // 与采矿重复，应只导出一次
            botany.Add(Entry(7777, false));

            var presets = GatheringNotebook.BuildPresets(mining, botany, "采矿", "园艺", "{0} 未采集 {1}", "共 {0} 项");
            Expect(presets.Count == 5);
            Expect(presets[0].ItemIds.Count == 100 && presets[1].ItemIds.Count == 100 && presets[2].ItemIds.Count == 50);
            Expect(presets[3].ItemIds.Count == 100 && presets[4].ItemIds.Count == 20);
            Expect(presets[0].Name == "采矿 未采集 1" && presets[2].Name == "采矿 未采集 3");
            Expect(presets[3].Name == "园艺 未采集 1");

            var allIds = presets.SelectMany(preset => preset.ItemIds).ToList();
            Expect(allIds.Count == allIds.Distinct().Count());
            Expect(!allIds.Contains(7777u));
            Expect(allIds.Count == 370);
            Expect(presets.All(preset => preset.Base64.Length > 0));

            var decoded = DecodePreset(presets[0].Base64);
            Expect(decoded.Version == 6);
            using var document = JsonDocument.Parse(decoded.Json);
            var root = document.RootElement;
            Expect(root.GetProperty("Name").GetString() == "采矿 未采集 1");
            Expect(root.GetProperty("ItemIds").GetArrayLength() == 100);
            Expect(root.GetProperty("Quantities").GetProperty("1000").GetUInt32() == 1);
            Expect(root.GetProperty("EnabledItems").GetProperty("1000").GetBoolean());
            Expect(!root.GetProperty("PrefferedLocations").EnumerateObject().Any());
            Expect(!root.GetProperty("Enabled").GetBoolean() && !root.GetProperty("Fallback").GetBoolean());
        });

        Check("Ruri order json", () =>
        {
            var entries = new[]
            {
                new GatheringEntry { ItemId = 5509, Selected = true },
                new GatheringEntry { ItemId = 5509, Selected = true },
                new GatheringEntry { ItemId = 5380, Selected = false },
                new GatheringEntry { ItemId = 7777, Selected = true }
            };

            var json = GatheringNotebook.BuildRuriOrderJson(entries);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            Expect(root.GetArrayLength() == 2);
            Expect(root[0].GetProperty("ItemId").GetUInt32() == 5509);
            Expect(root[0].GetProperty("Quantity").GetInt32() == 1);
            Expect(root[0].GetProperty("SourceIntent").GetString() == "Gather");
            Expect(!root[0].GetProperty("SkipIfEnough").GetBoolean());
            Expect(root[1].GetProperty("ItemId").GetUInt32() == 7777);
            Expect(GatheringNotebook.BuildRuriOrderJson(Array.Empty<GatheringEntry>()) == string.Empty);
        });

        Console.WriteLine($"PASS {passed} test groups against real Omni API 1 dependencies.");
        return Task.CompletedTask;
    }

    private static (byte Version, string Json) DecodePreset(string base64)
    {
        using var input = new MemoryStream(Convert.FromBase64String(base64));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        var bytes = output.ToArray();
        return (bytes[0], Encoding.UTF8.GetString(bytes, 1, bytes.Length - 1));
    }

    private static void Expect(bool success)
    {
        if (!success) throw new Exception("Assertion failed");
    }

    private static void Check(string name, Action action)
    {
        action();
        Console.WriteLine("PASS " + name);
        passed++;
    }
}
