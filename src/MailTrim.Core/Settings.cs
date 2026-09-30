namespace MailTrim.Core;

public sealed class AccountProfile : System.ComponentModel.INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Личный ящик";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    [System.Text.Json.Serialization.JsonIgnore] public MailboxStatus Status { get; private set; } = new("Ещё не проверено", null, null);
    [System.Text.Json.Serialization.JsonIgnore] public string TabLabel => Name + (Status.CheckedAt is null ? "" : Status.Unread is { } n ? $" · {n}" : "");
    [System.Text.Json.Serialization.JsonIgnore] public string UnreadBadge => Status.CheckedAt is null ? "" : Status.Unread is { } n ? n.ToString() : "";
    [System.Text.Json.Serialization.JsonIgnore] public string StatusText => Status.Description;
    public void SetStatus(MailboxStatus status)
    {
        Status = status;
        PropertyChanged?.Invoke(this, new(nameof(UnreadBadge))); PropertyChanged?.Invoke(this, new(nameof(TabLabel))); PropertyChanged?.Invoke(this, new(nameof(StatusText)));
    }
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
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool NotifyNewMail { get; set; } = true;
    public bool UseSiteNotifications { get; set; }
}



