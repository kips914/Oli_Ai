# OLI 1.10.2 — приложение снова запускается

## Что исправлено

**OLI.exe падал при запуске.** В логе было:

```
System.Windows.Markup.XamlParseException
  → System.ArgumentNullException: Значение не может быть NULL. (Parameter 'property')
     в System.Windows.Application.DoStartup()
```

Причина: в словаре стилей приложения (`App.xaml`) дважды использовался приём
`<Setter Property="Resources">`. Свойство `Resources` **не является**
DependencyProperty, а `Setter` умеет ставить только DP — при разборе словаря
WPF бросал исключение, окно не создавалось, и `OLI.exe` закрывался.

Исправление:

- `<Setter Property="Resources">` убран из обоих мест;
- «тонкие» полосы прокрутки теперь задаются обычным неявным стилем
  `<Style TargetType="ScrollBar" BasedOn="{StaticResource ThinScrollBar}"/>`,
  поэтому они остаются во всём приложении — в чате, настройках, workspace;
- стиль `ThinScrollViewer` удалён, все ссылки на него убраны из `MainWindow.xaml`
  и `SettingsWindow.xaml` (иначе была бы ошибка «ресурс не найден»).

## Защита от повторения

- Главное окно создаётся в коде (`OnStartup`), а не через `StartupUri`. Любая
  ошибка XAML/ресурсов теперь показывает читаемое окно с текстом исключения
  и пишется в `oli.log`, вместо молчаливого падения.
- Все остальные `Setter` проверены: в стилях остались только реальные
  DependencyProperty.

## Обновление

- Windows: `OLI.exe` / `OLI-win-x64.zip` — просто замените файл.
- Android: `OLI-Mobile-1.10.2-release.apk` (версия 1.10.2, code 7).
