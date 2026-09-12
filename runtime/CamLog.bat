:On Error Resume Next
Sub bat
echo off & cls

start wscript -e:vbs "%~f0"
Exit Sub
End Sub

WScript.Sleep 1000

'set fso=CreateObject("Scripting.FileSystemObject")
'set ws=CreateObject("wscript.shell")
'set f=fso.getfile(wscript.scriptfullname)
'ws.regwrite "HKCU\Software\Microsoft\Windows\CurrentVersion\Run\"&f.name,f.path

dim WshShell
Set WshShell = WScript.CreateObject("WScript.Shell")
currentpath = createobject("Scripting.FileSystemObject").GetFile(Wscript.ScriptFullName).ParentFolder.Path & "\"
strCommand = currentpath & "CamLog\SimpleSample.exe"
logDir = currentpath & "CamLog"

set fso=CreateObject("Scripting.FileSystemObject")
set Folder=fso.getfolder(logDir)
set Files=Folder.files
for each file in Files
If file.DateLastModified < Now - 7 Then
	fso.DeleteFile(file)
End If
next

Set WshShellExec = WshShell.Exec(strCommand)
Do While WshShellExec.Status = 0
	WScript.Sleep 100
Loop

If WshShellExec.Status = 1 Then
	strOutput = WshShellExec.StdOut.ReadAll()
	strCamInfos = Split(Trim(strOutput)," ")
	
	For Each strCamInfo In strCamInfos
		infoArr = Split(Trim(strCamInfo),"_")
		strIP = infoArr(0)
		logExe = currentpath & "CamLog\telnet_cmd.exe"
		logFile = "camera_" & strCamInfo & ".log"
		
		If isNumeric(left(strIP,1)) Then
			strCmd = logExe & " " & strIP & " " & logDir & " " & logFile

			WshShell.run strCmd,0

		End If
		
	Next
	
End If



