namespace ReconPlatform.Skills;

// Stub — full implementation in Task 4.1
public sealed class SkillRegistry
{
    private readonly string _skillsDirectory;

    public SkillRegistry(string skillsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillsDirectory);
        _skillsDirectory = skillsDirectory;
    }

    public IReadOnlyList<string> GetLoadedSkillIds() => [];
}
