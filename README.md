# WSS C# Implementation

Bootstrap of the HFI WSS C# console application.

## Layout

- `src/` – contains the console application (`HFI_WSS_Csharp_Implementation.csproj`).
- `lib/` – drop any third-party `.dll` dependencies here (kept empty via `.gitkeep`).

## Getting Started

```bash
dotnet restore HFI_WSS_Csharp_Implementation.sln
dotnet build HFI_WSS_Csharp_Implementation.sln
dotnet run --project src/HFI_WSS_Csharp_Implementation.csproj
```

Use the solution file if you plan to add additional projects (tests, tooling, etc.).
