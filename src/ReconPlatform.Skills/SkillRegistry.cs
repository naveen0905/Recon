namespace ReconPlatform.Skills;

// Stub — full implementation in Task 4.1
// _skillsDirectory field stored when FileSystemWatcher is wired up in Task 4.1
public sealed class SkillRegistry
{
    public SkillRegistry(string skillsDirectory)
        => ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);

    public IReadOnlyList<string> GetLoadedSkillIds() => [];
}
