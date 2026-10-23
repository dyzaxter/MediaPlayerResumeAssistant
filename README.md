# Media Player Resume Assistant

A lightweight Windows desktop app that remembers where you stopped watching
a video and automatically resumes it from that position in Windows Media Player.

The app doesn't replace your player: videos still open in the default Windows
application, while the assistant tracks playback in the background through the
Windows media session (SMTC) and restores your last saved position.

## Features

- **Automatic resume**: saves your playback position and seeks back to it when you reopen a video.
- **Watch history**: keeps a "recently played" library sorted by last playback, including videos on removable drives (e.g. USB) that aren't currently connected.
- **Safe history management**: missing files can be removed from the history or kept; deleting a file from disk always asks for confirmation.
- **Battery-friendly polling**: checks the position every 5 s while tracking, backs off to 30 s when idle, and refreshes media metadata at most every 60 s.
- **Persistent session detection**: keeps looking for the player session if it takes time to appear, and only controls sessions identified as Windows Media Player.
- **Groove-inspired UI**: dark theme with purple/magenta accents, sidebar, rounded cards, and adjustable UI scale (slider or Ctrl + mouse wheel).
- **Crash-safe progress**: position is saved to disk periodically and flushed on exit.

## Build

Requires the .NET 8 SDK (Windows). Run `Compilar.bat`, or:

    dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true

Data is stored in `%LOCALAPPDATA%\MediaPlayerResumeAssistant\positions.json`.

## Tech stack

C# · WPF · .NET 8 · Windows Media Control (SMTC)
