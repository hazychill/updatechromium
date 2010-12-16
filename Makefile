CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

updatechromium.exe: updatechromium.cs SettingsManager.dll
	$(CSC) /debug /out:$@ /r:SettingsManager.dll updatechromium.cs

clean:
	del updatechromium.exe
	del updatechromium.pdb
