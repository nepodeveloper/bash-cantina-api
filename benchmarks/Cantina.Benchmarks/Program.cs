using BenchmarkDotNet.Running;
using Cantina.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(CatalogueBenchmarks).Assembly).Run(args);
