namespace LinuxMadeSane.Infrastructure.Persistence.Entities;

public sealed class LinuxShareGroupEntity
{
    public Guid Id { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string MembersJson { get; set; } = "[]";
}
