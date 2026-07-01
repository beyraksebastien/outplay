# Building a downloadable .exe

This produces a single, self-contained `OutplayOverlay.exe` that runs on any 64-bit Windows
machine — no .NET install required on the machine that runs it (the .NET runtime is bundled
inside the exe itself, so the file is larger than a normal build output, typically 100-150MB).

From `app/OutplayOverlay/`, run:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The resulting file is `publish/OutplayOverlay.exe`. That's the one file to share/download — copy
it anywhere and double-click to run, no installer, no separate `.dll`s needed alongside it.

## Notes

- **Windows Defender/SmartScreen**: since this isn't code-signed (no publisher certificate), first
  runs on a fresh machine may show a "Windows protected your PC" warning. Click "More info" → "Run
  anyway". This is normal for any unsigned indie app — getting rid of it requires purchasing a code
  signing certificate, which is a separate, optional step if this app is ever distributed more
  widely.
- **`sessions.db`**: still gets created under `%LOCALAPPDATA%\OutplayOverlay\` on first run, same
  as when running via `dotnet run` — the exe being portable doesn't change where app data lives.
- **Updating**: there's no auto-update mechanism — after making code changes, re-run the publish
  command and share the new `.exe` again. An installer with auto-update (e.g. via Squirrel or
  MSIX) is a reasonable next step if this app gets shared with people other than yourself, but is
  out of scope for now.
- **Icon**: the app currently has no custom icon (`<ApplicationIcon />` in the .csproj is empty),
  so it'll show the default .NET icon in Windows Explorer/taskbar. Cosmetic, not blocking.
