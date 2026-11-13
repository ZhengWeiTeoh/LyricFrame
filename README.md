# LyricFrame

A Windows tray app that puts the time, current and next YouTube Music tracks, and lyrics on a display of your choice.

## Run

1. Extract the release ZIP and double-click **LyricFrame.exe**.
2. In Brave, Chrome, or Edge, open the Extensions page, enable **Developer mode**, choose **Load unpacked**, and select the extracted **extension** folder.
3. Refresh YouTube Music and play a song. Open **Up next** if the queue title is blank. The extension badge shows **ON** when the app accepts track updates.

Use the tray menu to change the background, select a display, or toggle **Start with Windows**. The app uses a blue gradient by default. Choose your own picture with **Change background**.

## Build

On Windows, run the following from PowerShell in the source folder:

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /out:LyricFrame.exe /win32icon:LyricFrame.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll MusicDesktop.cs DesktopSurface.cs
```

For a release ZIP, include **LyricFrame.exe**, **README.md**, and the **extension** folder. The browser extension sends YouTube Music playback information to LyricFrame on the same PC. LyricFrame sends track metadata to LRCLIB and Lyrics.ovh for lyrics lookup.
