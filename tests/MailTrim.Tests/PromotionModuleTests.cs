using MailTrim.Core;

internal static class PromotionModuleTests
{
    public static void Run(Action<bool, string> check)
    {
        const string mail = "https://e.mail.ru/inbox/";
        const string module = "https://img.imgsmail.ru/2promo.octavius.bundle.js";
        var rules = new FilterRules();
        check(PromotionModulePolicy.ShouldBlockScript(module, mail, rules, true),
            "dedicated Mail promotion script is blocked independently of its campaign text");
        check(PromotionModulePolicy.ShouldBlockScript("https://e.mail.ru/assets/hash-123/2promo.octavius.bundle.js?build=456#module", mail, rules, true)
            && PromotionModulePolicy.ShouldBlockScript("https://cdn.example.test/path/2promo.octavius.bundle.js", mail, rules, true),
            "dedicated module matches hashed folders and queries on either first-party or external resource hosts");
        check(!PromotionModulePolicy.ShouldBlockScript(module, mail, rules, false),
            "pausing request filtering allows the promotion module");
        foreach (var document in new[]
        {
            "https://account.mail.ru/login/", "https://id.vk.com/auth", "https://cloud.mail.ru/home/",
            "https://example.test/", "https://e.mail.ru.evil.test/inbox/", "http://e.mail.ru/inbox/",
            "https://e.mail.ru:444/inbox/", "https://user@e.mail.ru/inbox/", "about:blank", "not a URL"
        })
            check(!PromotionModulePolicy.ShouldBlockScript(module, document, rules, true),
                "promotion module policy does not run outside the exact official Mail document " + document);
        foreach (var request in new[]
        {
            "http://img.imgsmail.ru/2promo.octavius.bundle.js", "https://img.imgsmail.ru:444/2promo.octavius.bundle.js",
            "https://user:pass@img.imgsmail.ru/2promo.octavius.bundle.js", "file:///2promo.octavius.bundle.js",
            "data:text/javascript,2promo.octavius.bundle.js", "/2promo.octavius.bundle.js", "not a URL",
            "https://img.imgsmail.ru/12promo.octavius.bundle.js", "https://img.imgsmail.ru/x2promo.octavius.bundle.js",
            "https://img.imgsmail.ru/2promo.octavius.bundle.js.extra", "https://img.imgsmail.ru/2promo.octavius.bundle.js.map",
            "https://img.imgsmail.ru/2promo.octavius.bundle.js/nested.js", "https://img.imgsmail.ru/2promo.octavius.bundle.js/",
            "https://img.imgsmail.ru/2promo.OCTAVIUS.bundle.js", "https://img.imgsmail.ru/main.octavius.bundle.js",
            "https://e.mail.ru/api/messages?file=2promo.octavius.bundle.js", "https://e.mail.ru/assets/promo.js",
            "https://img.imgsmail.ru/modal.bundle.js", "https://account.mail.ru/auth.bundle.js"
        })
            check(!PromotionModulePolicy.ShouldBlockScript(request, mail, rules, true),
                "promotion module policy keeps non-matching or invalid request " + request);
        rules.AllowedDomains.Add("imgsmail.ru");
        check(!PromotionModulePolicy.ShouldBlockScript(module, mail, rules, true),
            "explicit allowed resource domain overrides the dedicated promotion module block");
        check(PromotionModulePolicy.ShouldBlockScript("https://other.test/2promo.octavius.bundle.js", mail, rules, true),
            "an allowed domain does not exempt a different promotion resource host");
    }
}
