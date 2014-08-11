IF EXIST bin rmdir bin /S /Q
IF EXIST obj rmdir obj /S /Q
msbuild FalconUDP.sln /p:Configuration=Debug
msbuild FalconUDP.sln /p:Configuration=Release
msbuild FalconUDPMono.msbuild /p:Configuration=Debug
msbuild FalconUDPMono.msbuild /p:Configuration=Release
msbuild FalconUDPMonoLinux.msbuild /p:Configuration=Debug
msbuild FalconUDPMonoLinux.msbuild /p:Configuration=Release