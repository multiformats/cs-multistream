#!/usr/bin/env bash

mono=${TEST_MONO:=0}
dotnet=${TEST_DOTNET:=0}

case "$1" in
  dotnet)
    dotnet=1
    mono=0
    ;;
  mono)
    mono=1
    dotnet=0
    ;;
esac

echo "* settings: mono=$mono, dotnet=$dotnet"

set -e

if [ $dotnet -eq 1 ]; then
  echo "* building and testing dotnet"

  if [ "$TRAVIS_OS_NAME" == "osx" ]; then
    ulimit -n 1024
    dotnet restore --disable-parallel --runtime osx-x64
  else
    dotnet restore --runtime ubuntu-x64
  fi

  dotnet test ./test/Multiformats.Stream.Tests/Multiformats.Stream.Tests.csproj -c Release -f netcoreapp2.1
  dotnet build ./test/Multiformats.Stream.Tests/Multiformats.Stream.Tests.csproj -c Release -f net461
fi

if [ $mono -eq 1 ]; then
  echo "* building and testing mono"
  export FrameworkPathOverride=$(dirname $(which mono))/../lib/mono/4.5/
  mono --framework=v4.0 nuget.exe restore
  xbuild ./test/Multiformats.Stream.Tests/Multiformats.Stream.Tests.csproj /p:Configuration=Release /p:Platform:net461
  mono $HOME/.nuget/packages/xunit.runner.console/*/tools/net452/xunit.console.exe ./test/Multiformats.Stream.Tests/bin/Release/net461/Multiformats.Stream.Tests.dll
fi
