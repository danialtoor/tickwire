using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Tickwire.Benchmarks.CodecBenchmarks).Assembly).Run(args);
