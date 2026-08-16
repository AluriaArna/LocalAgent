using System.IO;

namespace AgentUi;

public class SystemUser
{
    public string Name { get; set; } = "";
    public int Uid { get; set; }
    public int Gid { get; set; }
    public string Home { get; set; } = "";
    public string Shell { get; set; } = "";

    public bool IsRegularUser => Uid >= 1000 && Uid < 65534 && Name != "nobody";

    public override string ToString() => $"{Name} (UID {Uid}) - {Home}";

    public static List<SystemUser> LoadAll()
    {
        var users = new List<SystemUser>();
        if (!File.Exists("/etc/passwd")) return users;

        foreach (var line in File.ReadAllLines("/etc/passwd"))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
            var parts = line.Split(':');
            if (parts.Length < 7) continue;

            users.Add(new SystemUser
            {
                Name = parts[0],
                Uid = int.TryParse(parts[2], out var uid) ? uid : 0,
                Gid = int.TryParse(parts[3], out var gid) ? gid : 0,
                Home = parts[5],
                Shell = parts[6]
            });
        }

        return users;
    }
}