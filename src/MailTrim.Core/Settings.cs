namespace MailTrim.Core;

public sealed class AccountProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Личный ящик";
    public override string ToString() => Name;
}

public sealed class AppSettings
{
    public List<AccountProfile> Profiles { get; set; } = [new()];
    public Guid? ActiveProfile { get; set; }
    public bool BlockRequests { get; set; } = true;
    public bool CosmeticFilters { get; set; } = true;
    public bool Aggressive { get; set; }
    public string Theme { get; set; } = "System";
    public string UpdateRepository { get; set; } = "lineEdit/MailTrim";
    public bool CheckUpdatesOnStartup { get; set; }
}
