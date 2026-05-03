using BenchmarkDotNet.Running;

// Use BenchmarkSwitcher so the binary supports `--filter`, `--list`, etc.
// Examples:
//   dotnet run -c Release --project bench/B3.Exchange.Bench -- --filter '*Matching*'
//   dotnet run -c Release --project bench/B3.Exchange.Bench -- --list flat
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

internal sealed partial class Program;
