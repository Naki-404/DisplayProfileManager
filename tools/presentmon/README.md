# tools\presentmon — Intel PresentMon (not checked in)

This folder is where Visor looks for `PresentMon.exe`, the capture tool behind the FPS monitor.
**No binary is committed here.** Either run `build-release.ps1 -FetchPresentMon` (pinned release,
SHA-256 verified) or drop the file in by hand:

1. Download the console build (`PresentMon-<version>-x64.exe`) from the official releases page:
   https://github.com/GameTechDev/PresentMon/releases
2. Rename it to `PresentMon.exe` and put it in one of:
   - `<Visor install folder>\tools\presentmon\PresentMon.exe`
   - `%LocalAppData%\Visor\tools\presentmon\PresentMon.exe`
   - any path you set in Visor's settings (file or folder).
3. Visor accepts the file only if it is Authenticode-signed by Intel **or** its SHA-256 matches
   the pinned list in `PresentMonLocator.PinnedHashes`. Anything else shows as "unverified" and
   is never launched.

Visor never runs PresentMon on its own: a capture starts only when you press the button, and
Windows then shows a UAC prompt (ETW tracing needs administrator rights). Details, limits and
the CLI flags used are in `docs\MONITOR-PRESENTMON.md`.

## Attribution

PresentMon is Copyright (C) 2017-2024 Intel Corporation and is distributed under the MIT License.
Source: https://github.com/GameTechDev/PresentMon. This notice ships next to the bundled binary.

```
MIT License

Copyright (C) 2017-2024 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
