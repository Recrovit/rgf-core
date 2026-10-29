# RecroGrid Framework Core

[![NuGet Version](https://img.shields.io/nuget/v/RecroGrid.svg?label=Latest%20release)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Core/)
[![NuGet Version](https://img.shields.io/nuget/v/RecroGrid.svg?label=RecroGrid)](https://www.nuget.org/packages/RecroGrid/)

# RecroGrid Framework Client Packages

[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Abstraction.svg?label=Recrovit.RecroGridFramework.Abstraction)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Abstraction/)
[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Client.svg?label=Recrovit.RecroGridFramework.Client)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Client/)
[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Client.Blazor.svg?label=Recrovit.RecroGridFramework.Client.Blazor)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Client.Blazor/)
[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Client.Blazor.UI.svg?label=Recrovit.RecroGridFramework.Client.Blazor.UI)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Client.Blazor.UI/)
[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Blazor.RgfApexCharts.svg?label=Recrovit.RecroGridFramework.Blazor.ApexCharts)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Blazor.ApexCharts/)

[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Client.Blazor.Host.OpenIdConnect.svg?label=Recrovit.RecroGridFramework.Client.Blazor.Host.OpenIdConnect)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Client.Blazor.Host.OpenIdConnect/)
[![NuGet Version](https://img.shields.io/nuget/v/Recrovit.RecroGridFramework.Client.Blazor.SessionAuth.svg?label=Recrovit.RecroGridFramework.Client.Blazor.SessionAuth)](https://www.nuget.org/packages/Recrovit.RecroGridFramework.Client.Blazor.SessionAuth/)

Official Website: [RecroGrid Framework](https://RecroGridFramework.com)

## Initialization

```csharp
using Recrovit.RecroGridFramework.Extensions;

builder.AddRGF();
```

Core initializes general RGF services and capabilities without AI dependencies. For Recroby, install `Recrovit.RecroGridFramework.AI.Recroby` and call `AddRGFWithRecroby()` from `Recrovit.RecroGridFramework.AI.Recroby.Extensions`. Core-only hosts report `recrobyEnabled: false`.

## Developer documentation

- [Core initialization](https://github.com/Recrovit/rgf-docs/blob/dev/content/rgf-core/docs/core-initialization.md)
- [Recroby architecture and integration](https://github.com/Recrovit/rgf-docs/blob/dev/content/rgf-recroby/docs/architecture/recroby.md)
- [AI Credit integration](https://github.com/Recrovit/rgf-docs/blob/dev/content/rgf-recroby/docs/ai-credit-integration.md)
