```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5 (25F71) [Darwin 25.5.0]
Apple M2, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.401
  [Host]     : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.12 (10.0.12, 10.0.1226.42308), Arm64 RyuJIT armv8.0-a


```
| Method                  | Mean      | Error     | StdDev    | Gen0   | Allocated |
|------------------------ |----------:|----------:|----------:|-------:|----------:|
| ParseNewOrderSingle     | 299.96 ns |  1.155 ns |  0.964 ns | 0.0420 |     352 B |
| ParseExecutionReport    | 345.19 ns |  3.956 ns |  3.700 ns | 0.0477 |     400 B |
| SerializeNewOrderSingle | 383.28 ns |  2.470 ns |  2.062 ns | 0.0048 |      40 B |
| FrameNewOrderSingle     |  10.04 ns |  0.103 ns |  0.080 ns |      - |         - |
| ValidateNewOrderSingle  | 949.24 ns | 11.532 ns | 10.223 ns | 0.0458 |     384 B |
| Checksum                |  11.73 ns |  0.097 ns |  0.081 ns |      - |         - |
