using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;

using static StartupPorts.UiText;

namespace StartupPorts;

internal sealed record StartupGroup(Guid Id, string Name, IReadOnlyList<Guid> Members);

internal sealed record GroupMember(string Name, BitmapSource? Icon);

internal sealed class StartupGroupCard(StartupGroup group, IReadOnlyList<GroupMember> members)
{
    public StartupGroup Group { get; } = group;
    public string Name => Group.Name;
    public IReadOnlyList<GroupMember> Members { get; } = members;
    public string MemberCount { get; } = L("GroupMemberCount", members.Count);
    public IEnumerable<GroupMember> Preview => Members.Take(4);
    public string Overflow => Members.Count > 4 ? $"+{Members.Count - 4}" : "";
    public string Tooltip => string.Join(Environment.NewLine, Members.Select(member => member.Name).Prepend(Name));

    public bool Matches(StartupGroupCard other) => Group.Id == other.Group.Id && Name == other.Name &&
        MemberCount == other.MemberCount && Group.Members.SequenceEqual(other.Group.Members) && Members.SequenceEqual(other.Members);
}

internal sealed class StartupGroups(string path)
{
    public StartupGroups() : this(Path.Combine(Path.GetDirectoryName(AppIdentity.DataDirectory)!, "Groups.json")) { }

    public IReadOnlyList<StartupGroup> Read()
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var groups = JsonSerializer.Deserialize<List<StartupGroup>>(file);
            if (groups == null || groups.Any(group => group == null || group.Id == Guid.Empty ||
                    string.IsNullOrWhiteSpace(group.Name) || group.Members == null) ||
                groups.Select(group => group.Id).Distinct().Count() != groups.Count)
                throw new InvalidOperationException(L("InvalidGroups"));
            return groups;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    public void Save(StartupGroup group, bool create) => Change(groups =>
    {
        if (groups.Any(other => other.Id != group.Id && string.Equals(other.Name, group.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(L("GroupNameExists"));
        if (create && groups.All(other => other.Id != group.Id))
            groups.Add(group);
        else
            groups[Find(groups, group.Id)] = group;
    });

    public void AddMember(Guid groupId, Guid entryId) => Change(groups =>
    {
        var index = Find(groups, groupId);
        var group = groups[index];
        groups[index] = group with { Members = group.Members.Append(entryId).Distinct().ToArray() };
    });

    public void Delete(Guid id) => Change(groups => groups.RemoveAt(Find(groups, id)));

    private static int Find(List<StartupGroup> groups, Guid id)
    {
        var index = groups.FindIndex(group => group.Id == id);
        return index >= 0 ? index : throw new InvalidOperationException(L("GroupNotFound"));
    }

    private void Change(Action<List<StartupGroup>> change)
    {
        using var writer = new Mutex(false, $@"Global\StartupPorts.Groups.{AppIdentity.Sid}");
        try
        {
            if (!writer.WaitOne(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException(L("GroupsBusy"));
        }
        catch (AbandonedMutexException) { }

        try
        {
            var groups = Read().ToList();
            change(groups);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                    JsonSerializer.Serialize(file, groups);
                File.Move(temporary, path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        finally { writer.ReleaseMutex(); }
    }
}
