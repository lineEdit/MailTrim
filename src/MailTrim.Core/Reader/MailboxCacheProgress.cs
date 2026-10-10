namespace MailTrim.Core;

public enum CacheStage { Discovering, Saving, Complete, Stopped, Failed }

// Total is intentionally unknown until discovery has finished. No fabricated ETA.
public sealed record MailboxCacheProgress(CacheStage Stage, int Folders, int KnownFolders, int Found,
    int Total = 0, int Completed = 0, int Saved = 0, int Failed = 0, int Skipped = 0, int UncertainFolders = 0, bool DiscoveryComplete = false)
{
    public int Remaining => Math.Max(0, Total - Completed);
    public bool Running => Stage is CacheStage.Discovering or CacheStage.Saving;
    public bool Indeterminate => Stage == CacheStage.Discovering;
    public double Percent => Total == 0 ? Stage == CacheStage.Complete ? 100 : 0 : Math.Clamp(100.0 * Completed / Total, 0, 100);
    public string Description => Stage switch
    {
        CacheStage.Discovering => $"Поиск: папка {Folders}/{KnownFolders} · найдено {Found}. Общий объём уточняется…",
        CacheStage.Saving => $"Обработано {Completed}/{Total} · осталось {Remaining} · в кэше {Saved} · ошибок {Failed}",
        CacheStage.Complete => $"Готово: в кэше {Saved} · ошибок {Failed} · пропущено {Skipped}" + (UncertainFolders > 0 ? $" · не подтверждён конец папок: {UncertainFolders}" : ""),
        CacheStage.Stopped when !DiscoveryComplete => $"Поиск остановлен · найдено {Found}. Сохранённое осталось.",
        CacheStage.Stopped => $"Остановлено · в кэше {Saved} · осталось {Remaining}. Сохранённое осталось.",
        _ => "Кэширование отложено. Проверьте вход, сеть и место на диске; сохранённое осталось."
    };
}
