#!/usr/bin/env bash
# Dapper.SimpleCRUD master vs master + "cache per-type property lists" (ericdc1/Dapper.SimpleCRUD#283), side by side.
#
#   ./run.sh                    SQLite in memory, then PostgreSQL 16 (Docker)
#   ./run.sh sqlite             one database
#
# Downloads SimpleCRUD.cs and SimpleCRUDAsync.cs from both commits, compiles each into its own assembly
# (Dapper.SimpleCRUD.baseline / .cached, same settings as Dapper.SimpleCRUD.csproj) and runs BenchmarkDotNet
# after checking that both builds return identical rows and write identical data.
set -euo pipefail
cd "$(dirname "$0")"

# Colima: point Testcontainers at its socket when DOCKER_HOST is not set.
if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.colima/default/docker.sock" ]]; then
  export DOCKER_HOST="unix://$HOME/.colima/default/docker.sock" TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=/var/run/docker.sock
fi

BASELINE="${BASELINE:-https://raw.githubusercontent.com/ericdc1/Dapper.SimpleCRUD/master/Dapper.SimpleCRUD}"
CACHED="${CACHED:-https://raw.githubusercontent.com/rvegajr/Dapper.SimpleCRUD/perf/cache-type-metadata/Dapper.SimpleCRUD}"

for build in baseline cached; do
  url=$BASELINE; [[ $build == cached ]] && url=$CACHED
  mkdir -p "src/$build"
  for file in SimpleCRUD.cs SimpleCRUDAsync.cs; do
    curl -fsSL "$url/$file" -o "src/$build/$file"
  done
  cat > "src/$build/$build.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Dapper.SimpleCRUD.csproj's settings; only the assembly name differs so both builds load side by side. -->
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <AssemblyName>Dapper.SimpleCRUD.$build</AssemblyName>
    <DefineConstants>NETCORE;NETSTANDARD;NETSTANDARD2_0</DefineConstants>
    <NoWarn>\$(NoWarn);CS1591;CS8632</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.CSharp" Version="4.7.0" />
    <PackageReference Include="Dapper" Version="2.1.79" />
  </ItemGroup>
</Project>
CSPROJ
done
echo "baseline: $(git hash-object src/baseline/SimpleCRUD.cs 2>/dev/null || shasum src/baseline/SimpleCRUD.cs)"
echo "cached:   $(git hash-object src/cached/SimpleCRUD.cs 2>/dev/null || shasum src/cached/SimpleCRUD.cs)"

dotnet build bench -c Release -nologo -v quiet
for kind in "${@:-sqlite postgresql}"; do
  for k in $kind; do
    echo "==> $k"
    dotnet run --project bench -c Release --no-build -- "$k" --out "results/$k"
  done
done
echo "Results: $(pwd)/results/*/results/*-report-github.md"
