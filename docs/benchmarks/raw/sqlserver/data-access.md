```

BenchmarkDotNet v0.15.2, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.101
  [Host] : .NET 10.0.1 (10.0.125.57005), Arm64 RyuJIT AdvSIMD

Job=inproc-20  Toolchain=InProcessEmitToolchain  IterationCount=20  
IterationTime=250ms  WarmupCount=5  

```
| Type             | Method                         | Library          | Database  | Mean        | Error       | StdDev      | Gen0    | Gen1    | Allocated  |
|----------------- |------------------------------- |----------------- |---------- |------------:|------------:|------------:|--------:|--------:|-----------:|
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **Dapper**           | **sqlserver** |    **703.1 μs** |    **35.76 μs** |    **39.75 μs** |       **-** |       **-** |    **8.04 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | Dapper           | sqlserver |  1,124.6 μs |   120.87 μs |   139.19 μs |       - |       - |    8.45 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | Dapper           | sqlserver |    750.0 μs |    39.27 μs |    43.65 μs |       - |       - |   13.18 KB |
| DataAccessWrites | &#39;Update one&#39;                   | Dapper           | sqlserver |  1,075.4 μs |   114.24 μs |   131.56 μs |       - |       - |    7.29 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | Dapper           | sqlserver |  2,128.8 μs |   128.03 μs |   131.47 μs |       - |       - |   13.46 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | Dapper           | sqlserver | 64,201.4 μs | 2,559.92 μs | 2,845.34 μs |       - |       - |  403.96 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCore**           | **sqlserver** |    **723.6 μs** |    **45.52 μs** |    **52.42 μs** |       **-** |       **-** |   **14.12 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | EFCore           | sqlserver |  1,141.8 μs |    43.02 μs |    49.54 μs |       - |       - |   19.55 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCore           | sqlserver |    793.3 μs |    20.30 μs |    21.72 μs |  3.2895 |       - |   35.12 KB |
| DataAccessWrites | &#39;Update one&#39;                   | EFCore           | sqlserver |  1,138.9 μs |    67.51 μs |    75.03 μs |       - |       - |   19.33 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | EFCore           | sqlserver |  2,284.3 μs |   180.14 μs |   200.23 μs |       - |       - |   34.98 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | EFCore           | sqlserver |  6,016.0 μs |   476.02 μs |   548.19 μs | 73.1707 | 24.3902 |  780.33 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCoreNoTracking** | **sqlserver** |    **741.4 μs** |    **54.09 μs** |    **62.29 μs** |       **-** |       **-** |   **14.62 KB** |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCoreNoTracking | sqlserver |    832.0 μs |    86.91 μs |    96.60 μs |  2.9762 |       - |   26.82 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **SimpleCRUD**       | **sqlserver** |    **704.1 μs** |    **35.16 μs** |    **37.63 μs** |       **-** |       **-** |   **17.86 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | SimpleCRUD       | sqlserver |  1,161.3 μs |    28.49 μs |    30.48 μs |       - |       - |   11.85 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | SimpleCRUD       | sqlserver |    788.4 μs |    45.82 μs |    47.06 μs |  3.1250 |       - |   25.74 KB |
| DataAccessWrites | &#39;Update one&#39;                   | SimpleCRUD       | sqlserver |  1,162.0 μs |    46.10 μs |    51.24 μs |       - |       - |   15.31 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | SimpleCRUD       | sqlserver |  2,319.8 μs |   101.23 μs |   112.52 μs |       - |       - |    19.5 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | SimpleCRUD       | sqlserver | 67,989.3 μs | 2,965.06 μs | 3,172.58 μs |       - |       - | 1061.56 KB |
