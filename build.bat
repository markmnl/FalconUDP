IF EXIST bin rmdir bin /S /Q
IF EXIST obj rmdir obj /S /Q
msbuild FalconUDP.sln /p:Configuration=Debug
msbuild FalconUDP.sln /p:Configuration=MonoLinuxDebug
msbuild FalconUDP.sln /p:Configuration=Release
msbuild FalconUDP.sln /p:Configuration=MonoLinuxRelease