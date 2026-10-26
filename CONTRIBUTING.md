# helping out

Found something broken? open an issue with your NX Infinity version, game version, GPU, driver and what you were doing when it happened. screenshots help.

Keep games, keys, firmware and personal saves out of reports.

For code changes, keep the fix focused, include the checks you ran and leave the original credits in place.

Run `dotnet build src/NxInfinity.slnx -c Release` with .NET SDK 10.0.402. The included contract and service checks run with `dotnet run --project src/tests/NxInfinity.Runtime.ContractTests -c Release` and `dotnet run --project src/tests/NxInfinity.Runtime.Services.Tests -c Release`.
