using System.Reflection;
using System.Text;
using System.Text.Json;
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

            Expect(GatheringNotebook.Filter(entries, false, string.Empty).Count == 2);
            Expect(GatheringNotebook.Filter(entries, true, string.Empty).Count == 3);
            Expect(GatheringNotebook.Filter(entries, false, "矿").Count == 1);
            Expect(GatheringNotebook.Filter(entries, false, "  矿  ").Count == 1);
            Expect(GatheringNotebook.Filter(entries, false, "不存在").Count == 0);

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

        Console.WriteLine($"PASS {passed} test groups against real Omni API 1 dependencies.");
        return Task.CompletedTask;
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
