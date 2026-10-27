# NX Infinity

Launcher, runtime libraries, settings, input, profiles, local session services and the NX Infinity core host.

Build the launcher, runtime and tests with .NET SDK 10.0.402:

```text
dotnet build NxInfinity.slnx -c Release
```

Run the contract and service checks from the `tests` projects. The core host also requires the restored .NET 9 core tree; follow the [source baseline instructions](../build/source-baseline/README.md). Native x64/ARM64 archives use the [Linux packaging pipeline](../build/linux/README.md). Keep original notices with any changes.
