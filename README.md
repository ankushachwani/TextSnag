# TextSnag

Free, open-source screen text capture for macOS. Press a shortcut, drag over anything on
screen (a video, an image, a PDF, a locked web page), and the text is on your clipboard.

Everything runs on your Mac with Apple's built-in Vision text recognition. Nothing is
uploaded, there's no account, and the app is a ~1 MB menu-bar utility.

## Features

- **Start Capture** with a global shortcut (default **⌥⌘5**): native macOS crosshair, text goes straight to the clipboard
- **Custom shortcuts**: record any key combo in Settings → Shortcuts for
  - Start Capture
  - Capture Without Line Breaks
  - Capture Previous Area (re-grab the same spot, handy for videos and slides)
  - Toggle Additive Clipboard
- **Keep line breaks** or join lines into clean paragraphs (hyphenated words are re-joined)
- **Additive clipboard**: each capture is appended to the previous ones
- **QR codes and barcodes** are read in the same capture; optionally open their links
- **Read text aloud** after capturing
- Automatic language detection (every language macOS Vision supports)
- "Copied" bubble, optional sound, launch at login
- Menu bar only, no Dock icon

## Speed

About **0.05 s** from releasing the mouse to text on the clipboard for a typical selection,
about 0.2 s for a full screen of dense text (Apple Silicon, macOS 27).

How it stays fast:
- The screen grab is written as uncompressed TIFF to a private temp file (about 4x faster than PNG) and deleted immediately.
- QR detection runs in parallel with text recognition.
- The text recognizer is warmed up at launch. On macOS 27, the first time any app uses
  Apple's accurate text model, macOS spends up to a minute preparing it. That happens once,
  in the background, right after TextSnag's first launch; if you capture during that time
  you'll see "Getting ready (first launch only)".

## Install

1. Download `TextSnag.zip` from [Releases](../../releases), unzip, and drag **TextSnag.app** to Applications.
2. Open it. A viewfinder icon appears in the menu bar.
3. macOS asks for **Screen Recording** permission the first time. Turn on TextSnag in
   System Settings → Privacy & Security → Screen Recording, then quit and reopen TextSnag.

Requires macOS 13 Ventura or later (Apple Silicon or Intel).

## Build from source

Needs Xcode or the Command Line Tools (Swift 5.9+).

```sh
git clone https://github.com/<you>/TextSnag.git
cd TextSnag
scripts/build-app.sh            # builds and signs dist/TextSnag.app
scripts/build-app.sh --install  # also installs to /Applications and launches it
scripts/build-app.sh --zip      # also makes dist/TextSnag.zip for a release
```

Signing uses the first "Developer ID Application" certificate in your keychain, or an
ad-hoc signature if you don't have one. To notarize a release build, create a notarytool
profile (`xcrun notarytool store-credentials`) and run
`NOTARY_PROFILE=<profile> scripts/build-app.sh --zip`.

### Tests

```sh
swift build -c release
.build/release/TextSnag --selftest       # OCR, line-break handling, QR, timing
.build/release/TextSnag --ocr image.png  # recognize an image file
```

## How it works

| File | What it does |
| --- | --- |
| `AppDelegate.swift` | Menu bar menu, capture flow, clipboard, speech |
| `ScreenCapture.swift` | Runs macOS's `screencapture` for the selection UI |
| `OCR.swift` | Vision text + barcode recognition, line/paragraph formatting, warm-up |
| `HotKey.swift` | Global shortcuts (Carbon `RegisterEventHotKey`, no Accessibility permission needed) |
| `Settings.swift`, `SettingsWindow.swift` | Preferences and the shortcut recorder |
| `Toast.swift` | The "Copied" bubble |

## License

MIT
