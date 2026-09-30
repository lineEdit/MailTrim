namespace MailTrim.Core;

public static class SiteNotificationPolicy
{
    public static bool CanDeliver(AppSettings settings, string origin) =>
        settings.NotifyNewMail && settings.UseSiteNotifications && NavigationPolicy.IsMail(origin);
}
