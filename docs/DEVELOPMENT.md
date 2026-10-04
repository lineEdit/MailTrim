# Разработка

## Окружение

Для полной сборки и проверки нужны Windows 10/11 x64, .NET 10 SDK, PowerShell и WebView2 Evergreen Runtime. `global.json` задаёт минимальный SDK 10.0.100 с переходом на более новую feature band. Версия продукта задаётся в `Directory.Build.props`; отдельные проекты наследуют её.

## Структура

```text
src/
  MailTrim.App/         WPF-окна, ресурсы и точка входа
    Browser/           WebView2-сессии и проверка новой почты
    Reader/            Собственное чтение, источник данных и кэш
    Desktop/           Трей, автозапуск и положение окна
    Storage/           Настройки, правила и журнал
    Updates/           Проверка и установка обновления
    Diagnostics/       Изолированная проверка запуска дистрибутива
  MailTrim.Core/       Логика без WPF и зависимости от WebView2
    Filtering/         Сетевые правила, навигация и косметическая очистка
    Reader/            Парсер, нормализация, даты и действия в письме
    Mail/              Счётчики и обнаружение новой почты
    Updates/           Каталог релизов, проверка ZIP и передача запуска
tests/
  MailTrim.Tests/      Проверки основной логики
  MailTrim.UpdateTests/ Проверки установки и возврата к старой версии
  MailTrim.Smoke/      Интеграционные проверки WPF/WebView2 и HTML-фикстуры
rules/                 Встроенные правила и образцы для миграции
scripts/               Общие команды проверки и упаковки
docs/                  Руководства
.github/               Windows pipeline и шаблоны обращений
```

`artifacts/`, `bin/` и `obj/` — локальные результаты работы, не часть исходников. `rules/legacy-*.json` нужны для распознавания и миграции старых стандартных правил: удалять их как устаревшие файлы нельзя.

## Сборка и запуск

Выполняйте команды из корня репозитория:

```powershell
dotnet restore MailTrim.sln --locked-mode
dotnet build MailTrim.sln -c Release --no-restore
dotnet run --project src/MailTrim.App -c Release --no-build
```

Обычный запуск использует данные текущего пользователя Windows. Для автоматической проверки используйте тестовые проекты: они создают изолированные профили и не входят в личную почту.

```powershell
./scripts/Check.ps1
./scripts/Check.ps1 -NoBuild -ScratchRoot C:\Temp\mailtrim-check-unique
```

Без параметров скрипт восстанавливает зафиксированные зависимости, собирает решение и запускает все три набора проверок. Тестовый каталог должен ещё не существовать. По умолчанию создаётся уникальная папка внутри `artifacts/checks/`. Результаты не удаляются автоматически, чтобы их можно было исследовать. Подробности — в [TESTING.md](TESTING.md).

## Упаковка

```powershell
./scripts/Publish.ps1
./scripts/Publish.ps1 -OutputRoot C:\Temp\mailtrim-package-unique
```

Создаются папка `MailTrim-<версия>-win-x64`, ZIP, файл SHA-256 и `release-notes.md`. Дистрибутив включает .NET, правила, документацию и уведомления о лицензиях; WebView2 Runtime устанавливается отдельно. Упаковка требует записи для текущей версии в `CHANGELOG.md` и не публикует релиз сама.

Используется [single-file deployment .NET](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) без trimming. Управляемые библиотеки включены в EXE; нативные компоненты извлекаются средой .NET в её временный каталог. Символы встроены через `DebugType=embedded`. Инструмент анализа `Microsoft.NET.ILLink.Tasks` закреплён как зависимость сборки, чтобы смена SDK не меняла lock-файл неявно.

В корне пакета остаются `MailTrim.exe`, короткий `README.md`, `MailTrim.dll` и `MailTrim.runtimeconfig.json`. Последние два файла нужны старым версиям `UpdatePackage`, `UpdateInstaller` и `UpdateHandoff` для проверки состава и версии обновления. Рядом находятся `rules/`, `docs/` и `licenses/`; ссылки документации пересчитываются при упаковке. Лицензии .NET/WPF копируются из разрешённых runtime packs, лицензии WebView2 — из SDK.

`OutputRoot` позволяет собрать пакет отдельно от уже используемого дистрибутива. Скрипт отказывается перезаписывать существующую папку или архив этой версии: для повторной упаковки выберите новый `OutputRoot`. По умолчанию используется `artifacts/`.

## Зависимости и изменения

NuGet-источник задан в `NuGet.Config`. При осознанном обновлении пакета пересоздайте `src/MailTrim.App/packages.lock.json`, затем проверьте восстановление с `--locked-mode`. Не меняйте версии зависимостей в рамках обычного форматирования.

Используйте `.editorconfig` и `.gitattributes`. Пространства имён сохраняют границы проектов; каталоги обозначают подсистемы. WPF-ресурсы остаются рядом с окнами, чтобы их URI были стабильны. Версия в `assemblyIdentity` манифеста — идентификатор Win32, а версия приложения и файлов берётся из `Directory.Build.props`.

Связи компонентов описаны в [ARCHITECTURE.md](ARCHITECTURE.md), правила участия — в [CONTRIBUTING.md](../CONTRIBUTING.md), порядок публикации — в [RELEASING.md](RELEASING.md).
