# Clipboard Monitor

Clipboard Monitor is a .NET Framework 4.8.1 console app that listens for Windows clipboard updates and logs event details in real time.

## Features

- Receives clipboard change notifications via a hidden message window
- Logs event metadata (local/UTC timestamp, sequence number, owner/foreground process)
- Reads clipboard snapshot details for each change event
- Console hotkeys:
  - `Ctrl+C` to stop
  - `Space` to clear the screen

## Requirements

- Windows
- .NET Framework 4.8.1
- Visual Studio 2022/2026 or MSBuild tools compatible with .NET Framework projects

## Build

1. Open `ClipboardMonitor.slnx` in Visual Studio.
2. Build the solution.

## Run

- Run from Visual Studio, or
- Execute the built binary from `bin\`.
