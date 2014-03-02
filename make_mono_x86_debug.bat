IF NOT EXIST .\bin\Mono\x86\Debug\ MD .\bin\Mono\x86\Debug\
dmcs -d:MONO;DEBUG -platform:x86 -unsafe -target:library -out:.\bin\Mono\x86\Debug\FalconUDP.dll *.cs