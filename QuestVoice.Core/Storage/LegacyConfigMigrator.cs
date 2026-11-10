using System.Text.Json;

namespace QuestVoice.Core;

public sealed record ConfigMigrationReport(
    string RootDirectory,
    int ImportedConfigFiles,
    int PreservedVoiceOverrides,
    int PreservedProfiles,
    IReadOnlyList<string> Notes);

/// <summary>
/// Non-destructive compatibility reader and migrator for legacy PreVoice / BetterGI configuration files.
/// Never deletes old files, model weights, reference audio, or cached WAVs (Docs/00 & Docs/08).
/// </summary>
public static class LegacyConfigMigrator
{
    public static ConfigMigrationReport InspectAndEnsureCompatible(string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot);
        var configDir = Path.Combine(root, "config");
        Directory.CreateDirectory(configDir);
        var notes = new List<string>();
        var importedFiles = 0;
        var overridesCount = 0;
        var profilesCount = 0;

        var pathsFile = Path.Combine(configDir, "paths.json");
        if (File.Exists(pathsFile))
        {
            importedFiles++;
            notes.Add("已识别现有 config/paths.json 路径配置（只读兼容保留）。");
        }

        var routingFile = Path.Combine(configDir, "multilingual-routing.json");
        if (File.Exists(routingFile))
        {
            importedFiles++;
            notes.Add("已识别 BetterGI / PreVoice 多语言路由配置 config/multilingual-routing.json。");
        }

        var overridesFile = Path.Combine(configDir, "voice-overrides.json");
        if (File.Exists(overridesFile))
        {
            importedFiles++;
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(overridesFile));
                overridesCount = map?.Count ?? 0;
                notes.Add($"已保留用户角色声线覆盖配置 config/voice-overrides.json（共 {overridesCount} 条）。");
            }
            catch (Exception ex)
            {
                notes.Add("读取 config/voice-overrides.json 警告：" + ex.Message);
            }
        }

        var profilesFile = Path.Combine(configDir, "voice-profiles.json");
        if (File.Exists(profilesFile))
        {
            importedFiles++;
            try
            {
                var store = new VoiceProfileStore(root);
                profilesCount = store.Configuration.Profiles.Count;
                notes.Add($"已加载 config/voice-profiles.json（共 {profilesCount} 条声线配置，版本 v{store.Revision}）。");
            }
            catch (Exception ex)
            {
                notes.Add("读取 config/voice-profiles.json 警告：" + ex.Message);
            }
        }

        var instancesFile = Path.Combine(configDir, "tts-instances.json");
        if (File.Exists(instancesFile))
        {
            importedFiles++;
            notes.Add("已识别 TTS 服务端点配置 config/tts-instances.json。");
        }

        if (importedFiles == 0)
        {
            notes.Add("当前工作区未包含外部私有配置文件；将使用无凭据默认安全配置，随时可导入旧配置目录。");
        }

        return new ConfigMigrationReport(root, importedFiles, overridesCount, profilesCount, notes);
    }
}
