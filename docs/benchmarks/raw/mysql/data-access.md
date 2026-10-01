```

BenchmarkDotNet v0.15.2, macOS 26.6.2 (25G83) [Darwin 25.6.0]
Apple M4 Max, 1 CPU, 16 logical and 16 physical cores
.NET SDK 10.0.101
  [Host] : .NET 10.0.1 (10.0.125.57005), Arm64 RyuJIT AdvSIMD

Job=inproc-20  Toolchain=InProcessEmitToolchain  IterationCount=20  
IterationTime=250ms  WarmupCount=5  

```
| Type             | Method                         | Library    | Database | Mean        | Error       | StdDev      | Gen0   | Allocated |
|----------------- |------------------------------- |----------- |--------- |------------:|------------:|------------:|-------:|----------:|
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **Dapper**     | **mysql**    |    **605.9 μs** |    **72.03 μs** |    **82.95 μs** |      **-** |      **8 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | Dapper     | mysql    |  1,119.1 μs |   102.71 μs |   118.29 μs |      - |   6.56 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | Dapper     | mysql    |    676.3 μs |    49.39 μs |    56.88 μs |      - |  13.02 KB |
| DataAccessWrites | &#39;Update one&#39;                   | Dapper     | mysql    |  1,073.8 μs |    45.07 μs |    48.22 μs |      - |   6.41 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | Dapper     | mysql    |  2,064.8 μs |    64.94 μs |    69.49 μs |      - |  11.69 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | Dapper     | mysql    | 23,699.1 μs | 2,851.65 μs | 3,051.23 μs |      - | 262.71 KB |
| **DataAccessReads**  | **&#39;Get by id&#39;**                    | **SimpleCRUD** | **mysql**    |    **584.3 μs** |    **47.94 μs** |    **55.21 μs** | **2.0161** |  **17.52 KB** |
| DataAccessWrites | &#39;Insert one&#39;                   | SimpleCRUD | mysql    |  1,082.9 μs |    52.47 μs |    58.32 μs |      - |  10.22 KB |
| DataAccessReads  | &#39;Filtered page (20 rows)&#39;      | SimpleCRUD | mysql    |    670.7 μs |    60.99 μs |    70.24 μs | 2.5000 |  23.42 KB |
| DataAccessWrites | &#39;Update one&#39;                   | SimpleCRUD | mysql    |  1,113.2 μs |    53.24 μs |    56.96 μs |      - |  14.08 KB |
| DataAccessWrites | &#39;Insert + delete&#39;              | SimpleCRUD | mysql    |  2,073.0 μs |    29.38 μs |    31.44 μs |      - |  17.97 KB |
| DataAccessWrites | &#39;Insert 100 (one transaction)&#39; | SimpleCRUD | mysql    | 24,912.7 μs | 2,020.34 μs | 2,074.74 μs |      - | 762.26 KB |
