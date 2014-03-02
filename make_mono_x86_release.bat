IF NOT EXIST .\bin\Mono\x86\Release\ MD .\bin\Mono\x86\Release\
dmcs -d:MONO;RELEASE -platform:x86 -unsafe -target:library -optimize -out:.\bin\Mono\x86\Release\FalconUDP.dll *.cs