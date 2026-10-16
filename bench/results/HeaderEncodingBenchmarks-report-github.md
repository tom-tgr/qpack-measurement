```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a


```
| Method  | Workload     | Mean       | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|-------- |------------- |-----------:|---------:|---------:|------:|--------:|----------:|------------:|
| **Today**   | **fb-req.qif**   |   **152.8 ns** |  **1.21 ns** |  **1.07 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman | fb-req.qif   |   705.0 ns |  3.99 ns |  3.74 ns |  4.61 |    0.04 |         - |          NA |
| Decode  | fb-req.qif   |   949.0 ns |  3.47 ns |  3.25 ns |  6.21 |    0.05 |         - |          NA |
|         |              |            |          |          |       |         |           |             |
| **Today**   | **fb-resp.qif**  |   **224.4 ns** |  **0.93 ns** |  **0.87 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman | fb-resp.qif  | 1,216.1 ns |  7.15 ns |  5.97 ns |  5.42 |    0.03 |         - |          NA |
| Decode  | fb-resp.qif  | 1,731.6 ns | 10.42 ns |  9.24 ns |  7.72 |    0.05 |         - |          NA |
|         |              |            |          |          |       |         |           |             |
| **Today**   | **grpc-req.qif** |   **201.2 ns** |  **1.31 ns** |  **1.23 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman | grpc-req.qif | 1,954.3 ns |  6.81 ns |  5.69 ns |  9.71 |    0.06 |         - |          NA |
| Decode  | grpc-req.qif | 3,613.4 ns | 18.33 ns | 17.14 ns | 17.96 |    0.13 |         - |          NA |
|         |              |            |          |          |       |         |           |             |
| **Today**   | **rest-req.qif** |   **162.3 ns** |  **1.32 ns** |  **1.23 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman | rest-req.qif | 2,119.5 ns | 19.33 ns | 18.08 ns | 13.06 |    0.14 |         - |          NA |
| Decode  | rest-req.qif | 3,477.1 ns | 16.52 ns | 14.65 ns | 21.43 |    0.18 |         - |          NA |
