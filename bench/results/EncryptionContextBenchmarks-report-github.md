```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a


```
| Method    | Bytes | Mean     | Error     | StdDev    |
|---------- |------ |---------:|----------:|----------:|
| **Aes128Gcm** | **1182**  | **1.942 μs** | **0.0260 μs** | **0.0217 μs** |
| **Aes128Gcm** | **1502**  | **1.955 μs** | **0.0120 μs** | **0.0106 μs** |
