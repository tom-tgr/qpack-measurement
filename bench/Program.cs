using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(HeaderEncodingBenchmarks).Assembly).Run(args);
