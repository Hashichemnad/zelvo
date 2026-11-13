# Licensing boundary (modules 12 and 13)

This repository is public during development, so it holds **only the licensing contract and a development stub**. License verification, installation fingerprinting, watermarking, the signing tool and keys live in the private `zelvo-licensing` repository.

## What lives here

| Project | Contents |
| --- | --- |
| `src/api/Zelvo.Licensing.Abstractions` | `ILicenseService`, `LicenseStatus`, `LicensedModules`, and `LicenseStartupCheck`, which stops a service at startup when the license is not valid. |
| `src/api/Zelvo.Licensing.Development` | `DevelopmentLicenseService`: allows every module and version, and **refuses to run when the environment is `Production`**. It also provides `AddZelvoLicensing()`. |

Every service (API and worker today) calls `services.AddZelvoLicensing()`. Feature code asks `ILicenseService` (for example `IsModuleLicensed(LicensedModules.Payments)`) and never checks licenses itself.

## How release builds swap the implementation

Services reference the development stub only when the MSBuild property `ZelvoLicensingProject` is empty. A release build checks out the private repository and passes its project:

```bash
dotnet publish src/api/Zelvo.Api/Zelvo.Api.csproj -c Release \
  -p:ZelvoLicensingProject=/path/to/zelvo-licensing/src/Zelvo.Licensing/Zelvo.Licensing.csproj
```

The private project must:

- implement `ILicenseService` from `Zelvo.Licensing.Abstractions`;
- expose `Zelvo.Licensing.LicensingServiceCollectionExtensions.AddZelvoLicensing(this IServiceCollection)` that registers its implementation and `LicenseStartupCheck`.

No code in this repository changes between development and release builds.

## Rules

- Never add license checks, fingerprinting, watermark logic or keys to this repository.
- Changes to `ILicenseService` are coordinated with the private repository in the same release.
