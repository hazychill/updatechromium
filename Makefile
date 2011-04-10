CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

all: updatechromium.exe suspend.exe resume.exe

updatechromium.exe: updatechromium.cs
	$(CSC) /debug /out:$@ /r:SettingsManager.dll updatechromium.cs

suspend.exe: suspend.cs
	$(CSC) /debug /out:$@ /r:SettingsManager.dll suspend.cs

resume.exe: resume.cs
	$(CSC) /debug /out:$@ /r:SettingsManager.dll resume.cs

clean:
	del updatechromium.exe
	del updatechromium.pdb
	del suspend.exe
	del suspend.pdb
	del resume.exe
	del resume.pdb
