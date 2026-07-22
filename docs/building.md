# Building from source

Back to the [README](../README.md).

> [!TIP]
> You don't need to do this as a regular user, grab a pre-built `.exe` from the
> [releases page](https://github.com/SSyl/PalworldServerLauncher/releases/latest) instead. This is only for
> building the app yourself, which requires the **.NET 10 SDK**.

From the repository root:

```powershell
dotnet build
dotnet test
dotnet run --project src\PalServerLauncher              # run it
dotnet publish src\PalServerLauncher -c Release         # build a single self-contained .exe
```

Pass launcher options after `--`, for example `dotnet run --project src\PalServerLauncher -- --console`.
