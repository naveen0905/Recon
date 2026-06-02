using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ReconPlatform.Skills.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ReconPlatform.Skills;

public sealed class SkillRegistry : IDisposable
{
    private readonly ILogger<SkillRegistry> _logger;
    private readonly ConcurrentDictionary<string, SkillDefinition> _skills = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _watcher;
    private readonly IDeserializer _deserializer;

    public SkillRegistry(string skillsDirectory, ILogger<SkillRegistry> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadAllSkills(skillsDirectory);

        _watcher = new FileSystemWatcher(skillsDirectory, "*.yaml")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true
        };

        _watcher.Created += (_, e) => ReloadSkillFile(e.FullPath);
        _watcher.Changed += (_, e) => ReloadSkillFile(e.FullPath);
        _watcher.Deleted += (_, e) => RemoveSkillFile(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            RemoveSkillFile(e.OldFullPath);
            ReloadSkillFile(e.FullPath);
        };
    }

    public IReadOnlyList<string> GetLoadedSkillIds() =>
        [.. _skills.Keys];

    public bool TryGetSkill(string id, out SkillDefinition? skill) =>
        _skills.TryGetValue(id, out skill);

    public IReadOnlyList<SkillDefinition> GetSkillsByTriggerType(string triggerType) =>
        [.. _skills.Values.Where(s => s.Enabled && string.Equals(s.Trigger.Type, triggerType, StringComparison.OrdinalIgnoreCase))];

    public void RegisterSkill(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        _skills[skill.Id] = skill;
        _logger.LogInformation("Skill registered: {SkillId}", skill.Id);
    }

    public void UnregisterSkill(string id)
    {
        if (_skills.TryRemove(id, out _))
        {
            _logger.LogInformation("Skill unregistered: {SkillId}", id);
        }
    }

    public void Dispose() => _watcher.Dispose();

    private void LoadAllSkills(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories))
        {
            ReloadSkillFile(file);
        }
    }

    private void ReloadSkillFile(string filePath)
    {
        try
        {
            var yaml = File.ReadAllText(filePath);
            var skill = _deserializer.Deserialize<SkillDefinition>(yaml);

            if (string.IsNullOrWhiteSpace(skill.Id))
            {
                _logger.LogWarning("Skipping skill file with missing id: {FilePath}", filePath);
                return;
            }

            _skills[skill.Id] = skill;
            _logger.LogInformation("Skill loaded: {SkillId} from {FilePath}", skill.Id, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse skill file: {FilePath}", filePath);
        }
    }

    private void RemoveSkillFile(string filePath)
    {
        // Find and remove any skill whose file path matches. Since we key by Id,
        // we scan values and remove matching entries.
        var toRemove = _skills
            .Where(kvp => string.Equals(
                Path.GetFileNameWithoutExtension(filePath),
                kvp.Key,
                StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in toRemove)
        {
            if (_skills.TryRemove(id, out _))
            {
                _logger.LogInformation("Skill removed: {SkillId} (file deleted: {FilePath})", id, filePath);
            }
        }
    }
}
