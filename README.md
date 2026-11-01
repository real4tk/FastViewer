# FastViewer

A fast photo and video viewer for Windows 10 and 11, laid out like the Windows 11 Photos app.
Switching between files is instant, videos too. Four looks (Photos, Clean, Liquid Glass, Midnight; dark or light)
and a choice of which buttons you see.

## Download

Get **FastViewer-x.y.z.zip** from the [latest release](https://github.com/real4tk/FastViewer/releases/latest),
unzip it, put `FastViewer.exe` in a folder where it can stay, and run it. No install needed.
Windows may say "Windows protected your PC" the first time because the app isn't signed:
click **More info** → **Run anyway**. After that FastViewer offers new versions by itself.

See [How to use FastViewer.txt](How%20to%20use%20FastViewer.txt) and [What's new.txt](What's%20new.txt).

## Building

No SDK needed: `build.cmd` compiles `FastViewer.exe` with the C# compiler that ships with Windows
(.NET Framework 4.8). The XAML files are embedded as resources and loaded at startup.

## How it's fast

The files before and after the current one are preloaded into hidden players, so "next" only swaps which
player is visible, and the old picture stays up until the new one is ready (no black flash).
`FastViewer.exe <file> --selftest 80` presses next quickly and logs how long each switch took.

## License

[MIT](LICENSE)
