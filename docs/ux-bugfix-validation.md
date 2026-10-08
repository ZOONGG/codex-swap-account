# UX/UI bugfix: проверка

Версия не менялась. Новая ветка, release, tag и push не создавались.

## Причины и исправления

1. Popup: в Compact закрывался список и терялся профиль-якорь; использовался весь overlay. Теперь PlacementTarget — кнопка выбранного профиля, Placement — Left с штатной коррекцией WPF на границах экрана. Компактный список остаётся до закрытия подробностей. Escape, outside click и существующие обработчики геометрии сохранены.
2. Добавление профиля: на рабочей машине `.codex-profiles` и `.codex` являются junction. Login-сервис отклонял эти корни до запуска входа. Перенесённые корни разрешены; ссылки на отдельный профиль и auth.json по-прежнему запрещены. Пустые/недопустимые и занятые имена получают сообщения RU/EN до входа. Проверены создание, повторное чтение списка и сохранение auth через fake runtime, отказ при дубликате и защита существующего профиля.
3. Stars/Crypto: TextBox автоматически передаёт Padding в PART_ContentHost, а шаблон дополнительно задавал тот же Margin. При высоте 50 DIP viewport текста составлял только 6 DIP вместо необходимых 20. Удалён повторный отступ; числовая логика, min/max и ручное редактирование сохранены. Проверка viewport падает на старом шаблоне.
4. Ручные лимиты/статусы: удалены редактор, пресеты, toggle, поля модели, методы записи/очистки, fallback-индикатор и строки обеих локалей. Сохранены управление автоматическим обновлением и кешированные снимки. Старые JSON-поля игнорируются; регрессия подтверждает сохранение автоматических данных и preference.
5. Scrollbar: глобальный стиль задавал одновременно Width=10 и Height=10 и оставлял системный шаблон. Теперь общий шаблон с полноразмерным Track, ресурсами темы, hover/drag thumb и разными размерами для vertical/horizontal. Support использует этот же стиль.
6. Settings: сворачивание native-окна с ShowInTaskbar=false оставляло Windows minimized-caption strip. Теперь после завершения native-уведомления окно восстанавливается и скрывается через Hide(). X также скрывает экземпляр, который повторно открывается контроллером; отдельное CloseForShutdown освобождает ресурсы при завершении приложения. Восстановление внутри StateChanged приводило к размеру 160×28 — отложенная обработка предотвращает это. Никакого переноса за экран или уменьшения размеров нет.

## Проверки

126 targeted tests прошли: ProfileLoginServiceTests, ProfileNameTests, ProfileStatusServiceTests, ProfileStatusStoreTests, SettingsServiceTests, SupportIntegrationTests, LocalizationCatalogTests, UsagePresentationTests.

```powershell
.dotnet/dotnet.exe test tests/CodexProfileOverlay.Tests -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~ProfileLoginServiceTests|FullyQualifiedName~ProfileNameTests|FullyQualifiedName~ProfileStatusServiceTests|FullyQualifiedName~ProfileStatusStoreTests|FullyQualifiedName~SettingsServiceTests|FullyQualifiedName~SupportIntegrationTests|FullyQualifiedName~LocalizationCatalogTests|FullyQualifiedName~UsagePresentationTests"
.dotnet/dotnet.exe build CodexProfileOverlay.sln -c Release -p:Platform=x64
.dotnet/dotnet.exe build tests/CodexProfileOverlay.UiChecks -c Release -p:Platform=x64
& tests/CodexProfileOverlay.UiChecks/bin/x64/Release/net8.0-windows/CodexProfileOverlay.UiChecks.exe
```

Release x64 build: 0 ошибок, 0 предупреждений. WPF UI checks проходят в RU/EN, light/dark: X/minimize/reopen с проверкой native IsWindowVisible и сохранения геометрии; привязка popup в Expanded/Compact; Stars/Crypto presets и ручное редактирование; viewport текста при масштабах layout 100/150/200%; вертикальный scrollbar.

UI checks используют временные данные и fake flow; не запускают controller приложения, реальные платежи или браузерную авторизацию. Layout scale не заменяет проверку физического DPI мониторов.

Локальная публикация: `publish.ps1` создаёт self-contained win-x64 EXE, portable ZIP, Inno Setup installer, build-info и SHA256SUMS в `artifacts`. Исходная версия сохранена.

## Осталось вручную

- Завершить реальный браузерный вход нового профиля и проверить его появление после перезапуска.
- Проверить popup у краёв экранов, Escape/outside click, смену геометрии Codex и Compact/Expanded/Auto на нескольких мониторах с разным DPI.
- Проверить Settings через X, _, overlay и трей после переключения на Codex; положение taskbar и смену монитора.
- Оценить hover/drag/прокрутку Settings и support и видимость сумм на физических DPI 100–200% в обеих темах.

## Изменённые файлы

- `docs/account-limits-ui.md`
- `src/CodexProfileOverlay.Core/Models/OverlaySettings.cs`
- `src/CodexProfileOverlay.Core/Models/ProfileStatusMetadata.cs`
- `src/CodexProfileOverlay.Core/Services/LocalizationCatalog.cs`
- `src/CodexProfileOverlay.Core/Services/ProfileLoginService.cs`
- `src/CodexProfileOverlay.Core/Services/ProfileName.cs`
- `src/CodexProfileOverlay.Core/Services/ProfileStatusService.cs`
- `src/CodexProfileOverlay.Core/Services/ProfileStatusStore.cs`
- `src/CodexProfileOverlay/App.xaml`
- `src/CodexProfileOverlay/OverlayController.cs`
- `src/CodexProfileOverlay/OverlayWindow.cs`
- `src/CodexProfileOverlay/SettingsWindow.cs`
- `src/CodexProfileOverlay/SupportWindow.xaml`
- `tests/CodexProfileOverlay.Tests/LocalizationCatalogTests.cs`
- `tests/CodexProfileOverlay.Tests/ProfileLoginServiceTests.cs`
- `tests/CodexProfileOverlay.Tests/ProfileNameTests.cs`
- `tests/CodexProfileOverlay.Tests/ProfileStatusServiceTests.cs`
- `tests/CodexProfileOverlay.Tests/ProfileStatusStoreTests.cs`
- `tests/CodexProfileOverlay.Tests/SettingsServiceTests.cs`
- `tests/CodexProfileOverlay.UiChecks/CodexProfileOverlay.UiChecks.csproj`
- `tests/CodexProfileOverlay.UiChecks/Program.cs`
- `docs/ux-bugfix-validation.md`
