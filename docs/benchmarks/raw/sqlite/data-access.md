```

BenchmarkDotNet v0.15.2, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.101
  [Host] : .NET 10.0.1 (10.0.125.57005), Arm64 RyuJIT AdvSIMD

Job=inproc-20  Toolchain=InProcessEmitToolchain  IterationCount=20  
IterationTime=250ms  WarmupCount=5  

```
| Type             | Method                         | Library          | Database | Mean         | Error      | StdDev     | Gen0     | Gen1    | Allocated |
|----------------- |------------------------------- |----------------- |--------- |-------------:|-----------:|-----------:|---------:|--------:|----------:|
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **Dapper**           | **sqlite**   |     **5.109 μs** |  **0.0453 μs** |  **0.0521 μs** |   **0.3731** |       **-** |    **3.1 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | Dapper           | sqlite   |    44.457 μs |  1.1213 μs |  1.2463 μs |   0.5180 |       - |   4.87 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | Dapper           | sqlite   |    18.055 μs |  0.0819 μs |  0.0876 μs |   0.9404 |       - |    8.1 KB |
| DataAccessWrites | &#39;Update one&#39;                   | Dapper           | sqlite   |    37.114 μs |  1.1927 μs |  1.3257 μs |   0.4130 |       - |    3.7 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | Dapper           | sqlite   |    82.440 μs |  1.9601 μs |  2.0129 μs |   0.6545 |       - |   6.71 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | Dapper           | sqlite   |   318.231 μs |  4.0335 μs |  4.3159 μs |  22.0588 |  1.2255 | 185.68 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCore**           | **sqlite**   |    **18.328 μs** |  **0.2984 μs** |  **0.3437 μs** |   **1.4642** |  **0.4624** |  **12.13 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | EFCore           | sqlite   |    62.512 μs |  1.4087 μs |  1.5073 μs |   1.8315 |  0.4579 |  16.11 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCore           | sqlite   |    41.839 μs |  0.4576 μs |  0.5270 μs |   3.8333 |  0.8333 |  32.03 KB |
| DataAccessWrites | &#39;Update one&#39;                   | EFCore           | sqlite   |    54.989 μs |  1.4525 μs |  1.4916 μs |   1.8939 |  0.6313 |  15.95 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | EFCore           | sqlite   |   108.765 μs |  3.9409 μs |  4.0470 μs |   3.4965 |  0.8741 |  29.44 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | EFCore           | sqlite   | 1,101.154 μs | 14.3310 μs | 15.3340 μs | 111.6071 | 35.7143 | 923.14 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **EFCoreNoTracking** | **sqlite**   |    **18.590 μs** |  **0.3422 μs** |  **0.3940 μs** |   **1.5315** |  **0.5105** |  **12.63 KB** |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | EFCoreNoTracking | sqlite   |    37.035 μs |  0.2927 μs |  0.3370 μs |   2.8409 |  0.8971 |  23.73 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **SimpleCRUD**       | **sqlite**   |    **11.994 μs** |  **0.1003 μs** |  **0.1155 μs** |   **1.5525** |       **-** |  **12.94 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | SimpleCRUD       | sqlite   |    50.456 μs |  1.4932 μs |  1.6597 μs |   1.0280 |       - |   8.41 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | SimpleCRUD       | sqlite   |    25.982 μs |  0.1423 μs |  0.1581 μs |   2.2879 |       - |  19.22 KB |
| DataAccessWrites | &#39;Update one&#39;                   | SimpleCRUD       | sqlite   |    44.814 μs |  1.1520 μs |  1.2326 μs |   1.2429 |       - |  11.57 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | SimpleCRUD       | sqlite   |    86.886 μs |  3.7104 μs |  3.8103 μs |   1.3966 |       - |  12.88 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | SimpleCRUD       | sqlite   |   978.678 μs | 20.0453 μs | 23.0841 μs |  88.2353 |  3.6765 | 750.72 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **SimpleCRUDStatic** | **sqlite**   |    **11.865 μs** |  **0.0626 μs** |  **0.0696 μs** |   **1.5708** |       **-** |  **12.94 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | SimpleCRUDStatic | sqlite   |    49.510 μs |  1.9156 μs |  2.0496 μs |   1.0212 |       - |   8.41 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | SimpleCRUDStatic | sqlite   |    25.742 μs |  0.1854 μs |  0.2135 μs |   2.2652 |       - |  19.22 KB |
| DataAccessWrites | &#39;Update one&#39;                   | SimpleCRUDStatic | sqlite   |    47.485 μs |  2.2309 μs |  2.3870 μs |   1.3258 |       - |  11.57 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | SimpleCRUDStatic | sqlite   |    88.959 μs |  3.4802 μs |  3.7238 μs |   1.3812 |       - |  12.88 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | SimpleCRUDStatic | sqlite   |   974.804 μs | 21.0559 μs | 24.2481 μs |  88.2353 |  3.6765 | 744.45 KB |
