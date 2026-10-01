```

BenchmarkDotNet v0.15.2, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.101
  [Host] : .NET 10.0.1 (10.0.125.57005), Arm64 RyuJIT AdvSIMD

Job=inproc-20  Toolchain=InProcessEmitToolchain  IterationCount=20  
IterationTime=250ms  WarmupCount=5  

```
| Type             | Method                         | Library          | Database   | Mean        | Error       | StdDev      | Gen0    | Gen1    | Allocated |
|----------------- |------------------------------- |----------------- |----------- |------------:|------------:|------------:|--------:|--------:|----------:|
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **Dapper**           | **postgresql** |    **323.5 μs** |    **25.75 μs** |    **27.56 μs** |       **-** |       **-** |   **3.54 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | Dapper           | postgresql |    335.0 μs |    20.16 μs |    20.71 μs |       - |       - |   3.96 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | Dapper           | postgresql |    418.4 μs |    34.41 μs |    39.63 μs |       - |       - |   8.52 KB |
| DataAccessWrites | &#39;Update one&#39;                   | Dapper           | postgresql |    351.8 μs |    23.70 μs |    27.30 μs |       - |       - |   4.43 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | Dapper           | postgresql |    669.5 μs |    59.99 μs |    66.68 μs |       - |       - |   6.29 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | Dapper           | postgresql | 31,403.1 μs | 4,641.08 μs | 5,158.55 μs |       - |       - | 197.43 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCore**           | **postgresql** |    **358.0 μs** |    **28.95 μs** |    **33.34 μs** |       **-** |       **-** |    **9.3 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | EFCore           | postgresql |    413.6 μs |    71.94 μs |    82.85 μs |  1.4881 |       - |  14.08 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCore           | postgresql |    478.4 μs |    21.52 μs |    23.02 μs |  2.0161 |       - |  29.29 KB |
| DataAccessWrites | &#39;Update one&#39;                   | EFCore           | postgresql |    384.8 μs |    50.18 μs |    53.69 μs |  1.4205 |       - |  13.95 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | EFCore           | postgresql |    720.7 μs |    70.67 μs |    81.38 μs |  2.6042 |       - |  24.57 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | EFCore           | postgresql |  2,630.7 μs |    93.44 μs |    91.77 μs | 93.7500 | 31.2500 | 768.66 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCoreNoTracking** | **postgresql** |    **349.2 μs** |    **23.46 μs** |    **27.01 μs** |       **-** |       **-** |   **9.79 KB** |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCoreNoTracking | postgresql |    455.0 μs |    17.23 μs |    19.16 μs |  1.7857 |       - |  20.99 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **SimpleCRUD**       | **postgresql** |    **342.2 μs** |    **25.82 μs** |    **28.70 μs** |  **1.4881** |       **-** |  **13.48 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | SimpleCRUD       | postgresql |    343.9 μs |    40.37 μs |    39.65 μs |       - |       - |   7.23 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | SimpleCRUD       | postgresql |    412.7 μs |    18.77 μs |    20.08 μs |  1.6892 |       - |  19.68 KB |
| DataAccessWrites | &#39;Update one&#39;                   | SimpleCRUD       | postgresql |    386.1 μs |    42.55 μs |    49.00 μs |  1.3021 |       - |  12.45 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | SimpleCRUD       | postgresql |    713.1 μs |    68.39 μs |    78.76 μs |       - |       - |  12.23 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | SimpleCRUD       | postgresql | 33,442.0 μs | 3,931.86 μs | 4,527.93 μs |       - |       - |    553 KB |
