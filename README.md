# TS3 audio browser

Окно с браузером, которое заходит на TeamSpeak 3 отдельным клиентом и передаёт в канал звук открытой страницы.

Канал сервера должен использовать кодек Opus Music. Личность и настройки сохраняются в `%AppData%\TsBrowser`.

## Сборка

Нужен .NET 8 SDK.

```bat
dotnet publish TsBrowser.csproj -c Release -r win-x64 --self-contained true -o publish
```

Запуск: `publish\TsBrowser.exe`.

На машине должен стоять Visual C++ Redistributable x64: его требует встроенный Chromium.
