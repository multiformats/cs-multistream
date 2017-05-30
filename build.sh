#!/usr/bin/env bash

set -e

if [ $TRAVIS_OS_NAME = "osx" ]; then
  ulimit -n 1024
  dotnet restore --disable-parallel --runtime osx-x64
else
  dotnet restore --runtime ubuntu-x64
fi

export FrameworkPathOverride=$(dirname $(which mono))/../lib/mono/4.5/

dotnet test ./test/Multiformats.Stream.Tests/Multiformats.Stream.Tests.csproj -c Release -f netcoreapp1.1
dotnet build ./test/Multiformats.Stream.Tests/Multiformats.Stream.Tests.csproj -c Release -f net461

mono $HOME/.nuget/packages/xunit.runner.console/2.2.0/tools/xunit.console.exe ./test/Multiformats.Stream.Tests/bin/Release/net461/Multiformats.Stream.Tests.dll
