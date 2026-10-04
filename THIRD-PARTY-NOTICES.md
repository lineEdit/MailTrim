# Third-party components

- Microsoft.Web.WebView2 SDK 1.0.4191.47, Microsoft. [Package and license](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4191.47). The SDK/native loader are subject to Microsoft's package license, not this project's MIT license.
- Microsoft Edge WebView2 Evergreen Runtime: installed separately under Microsoft's terms.
- .NET 10 / WPF: Microsoft and contributors, MIT and bundled third-party notices. Self-contained distributions retain the runtime's licenses and third-party notices under `licenses/dotnet/`; WebView2 SDK notices are under `licenses/WebView2/`. Single-file bundling does not change their licensing terms.

Mail.ru and VK names belong to their respective owners. No affiliation or endorsement is claimed.

## Mail.ru cosmetic rules in 0.1.1

The selectors `.layout__column_right-indented`, `.letter-list-item-adv`, `#app-canvas .layout__overflow-container > .advert-column`, `#app-canvas .layout__overflow-container > .slot`, `#message-sent__rb-center-container` and `.layer-sent-page__banner` are adapted from [RU AdList — specific_special.txt](https://github.com/easylist/ruadlist/blob/master/advblock/specific_special.txt), by RU AdList contributors (including Lain_13 and dimisa), under [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/). Changes: host prefixes removed because injection is restricted to the Mail.ru top document; selectors integrated into MailTrim JSON. These rule entries retain CC BY 3.0 terms; application code remains MIT. Original license/credits: [RU AdList](https://github.com/easylist/ruadlist/blob/master/advblock.txt).

Additional technical references used to identify Mail.ru placement names and first-party ad proxy behavior: [AdGuard Russian filter](https://github.com/AdguardTeam/AdguardFilters/blob/master/CyrillicFilters/RussianFilter/sections/specific.txt), [Mail.ru ad proxy report](https://github.com/AdguardTeam/AdguardFilters/issues/31088). The label-cleanup implementation and regression fixtures are written for MailTrim; no AdGuard executable scripts are bundled.

Lite does not include the .NET/WPF/WinForms Runtime: users install .NET 10 Desktop Runtime x64 separately. WebView2 SDK notices are included in both editions.
