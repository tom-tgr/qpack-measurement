```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.1 (25F80) [Darwin 25.5.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100
  [Host]     : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.0 (10.0.0, 10.0.25.52411), Arm64 RyuJIT armv8.0-a


```
| Method               | Workload     | Mean       | Error   | StdDev  | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------- |------------- |-----------:|--------:|--------:|------:|--------:|----------:|------------:|
| **Today**                | **fb-req.qif**   |   **181.7 ns** | **1.02 ns** | **0.91 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman              | fb-req.qif   |   787.5 ns | 2.09 ns | 1.96 ns |  4.33 |    0.02 |         - |          NA |
| DynamicTable         | fb-req.qif   |   401.8 ns | 0.81 ns | 0.76 ns |  2.21 |    0.01 |         - |          NA |
| DynamicTableIndexAll | fb-req.qif   |   393.5 ns | 0.94 ns | 0.88 ns |  2.17 |    0.01 |         - |          NA |
|                      |              |            |         |         |       |         |           |             |
| **Today**                | **fb-resp.qif**  |   **222.3 ns** | **1.40 ns** | **1.17 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman              | fb-resp.qif  | 1,138.0 ns | 2.67 ns | 2.37 ns |  5.12 |    0.03 |         - |          NA |
| DynamicTable         | fb-resp.qif  |   600.2 ns | 1.18 ns | 1.10 ns |  2.70 |    0.01 |         - |          NA |
| DynamicTableIndexAll | fb-resp.qif  |   591.9 ns | 1.53 ns | 1.35 ns |  2.66 |    0.01 |         - |          NA |
|                      |              |            |         |         |       |         |           |             |
| **Today**                | **grpc-req.qif** |   **197.3 ns** | **1.07 ns** | **1.01 ns** |  **1.00** |    **0.01** |         **-** |          **NA** |
| Huffman              | grpc-req.qif | 1,927.1 ns | 3.29 ns | 2.92 ns |  9.77 |    0.05 |         - |          NA |
| DynamicTable         | grpc-req.qif |   253.6 ns | 0.57 ns | 0.54 ns |  1.29 |    0.01 |         - |          NA |
| DynamicTableIndexAll | grpc-req.qif |   263.2 ns | 0.62 ns | 0.58 ns |  1.33 |    0.01 |         - |          NA |
|                      |              |            |         |         |       |         |           |             |
| **Today**                | **rest-req.qif** |   **150.6 ns** | **0.40 ns** | **0.37 ns** |  **1.00** |    **0.00** |         **-** |          **NA** |
| Huffman              | rest-req.qif | 2,025.0 ns | 5.62 ns | 4.98 ns | 13.45 |    0.05 |         - |          NA |
| DynamicTable         | rest-req.qif |   268.1 ns | 0.39 ns | 0.33 ns |  1.78 |    0.00 |         - |          NA |
| DynamicTableIndexAll | rest-req.qif |   283.2 ns | 0.36 ns | 0.34 ns |  1.88 |    0.00 |         - |          NA |
