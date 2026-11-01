@echo off
rem Builds FastViewer.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:FastViewer.exe /win32icon:FastViewer.ico ^
  /r:"%FW%\WPF\PresentationCore.dll" ^
  /r:"%FW%\WPF\PresentationFramework.dll" ^
  /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" ^
  /r:Microsoft.CSharp.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll ^
  /r:"%FW%\System.IO.Compression.dll" /r:"%FW%\System.IO.Compression.FileSystem.dll" ^
  /resource:Theme.xaml,FastViewer.Theme.xaml ^
  /resource:Window.xaml,FastViewer.Window.xaml ^
  FastViewer.cs Settings.cs Mini.cs VideoInfo.cs AssemblyInfo.cs Playback.cs Convert.cs Extras.cs FeatureTest.cs Updates.cs Shortcuts.cs FolderWatch.cs Background.cs Thumbs.cs Gallery.cs
