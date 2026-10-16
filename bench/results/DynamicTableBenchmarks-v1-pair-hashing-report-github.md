```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a


```
| Method       | Workload     | Mean       | Error    | StdDev   | Ratio | RatioSD | Allocated | Alloc Ratio |
|------------- |------------- |-----------:|---------:|---------:|------:|--------:|----------:|------------:|
| **Today**        | **fb-req.qif**   |   **189.0 ns** |  **0.68 ns** |  **0.63 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Huffman      | fb-req.qif   |   790.5 ns |  2.53 ns |  2.37 ns |  4.18 |    0.02 |         - |          NA |
| DynamicTable | fb-req.qif   |   934.4 ns |  6.06 ns |  5.37 ns |  4.94 |    0.03 |         - |          NA |
|              |              |            |          |          |       |         |           |             |
| **Today**        | **fb-resp.qif**  |   **215.8 ns** |  **0.59 ns** |  **0.49 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Huffman      | fb-resp.qif  | 1,124.6 ns |  2.60 ns |  2.31 ns |  5.21 |    0.02 |         - |          NA |
| DynamicTable | fb-resp.qif  | 1,192.5 ns |  8.49 ns |  7.94 ns |  5.53 |    0.04 |         - |          NA |
|              |              |            |          |          |       |         |           |             |
| **Today**        | **grpc-req.qif** |   **201.5 ns** |  **0.61 ns** |  **0.58 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Huffman      | grpc-req.qif | 2,142.0 ns |  3.02 ns |  2.68 ns | 10.63 |    0.03 |         - |          NA |
| DynamicTable | grpc-req.qif | 1,404.1 ns | 11.17 ns | 10.45 ns |  6.97 |    0.05 |         - |          NA |
|              |              |            |          |          |       |         |           |             |
| **Today**        | **rest-req.qif** |   **156.4 ns** |  **0.40 ns** |  **0.35 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Huffman      | rest-req.qif | 1,789.6 ns |  3.88 ns |  3.44 ns | 11.44 |    0.03 |         - |          NA |
| DynamicTable | rest-req.qif | 1,467.6 ns | 10.90 ns |  9.10 ns |  9.38 |    0.06 |         - |          NA |
