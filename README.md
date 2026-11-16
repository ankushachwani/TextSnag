# TextSnag

Free, open-source screen text capture for macOS. Press a shortcut, drag over anything on
screen (a video, an image, a PDF, a locked web page), and the text is on your clipboard.

Everything runs on your Mac with Apple's built-in Vision text recognition. Nothing is
uploaded, there's no account, and the app is an ~1 MB menu-bar utility.

## What makes it different

- **Direct Text: exact text, not a guess.** When you select part of a real app window (a
  dialog, a settings page, a web page that blocks copying), TextSnag copies the app's own
  characters through the macOS Accessibility API, so there are no recognition mistakes. It
  only does this when that text matches what OCR sees in your selection, so hidden or
  off-screen text never sneaks in. Images, PDFs and video still use OCR.
- **Code mode.** Captures that look like code are re-read with autocorrect off (no more
  `cmd` → "cod" or `}` → "M"), keep their indentation, get curly quotes and full-width
  characters turned back into normal ones, and light-on-dark terminals are also read
  inverted. Lone `}` lines aren't dropped.
- **Tables.** Captures that look like a table are copied as tab-separated columns, ready to
  paste into Numbers, Excel or Google Sheets, with a one-click Markdown version.
- **Quick actions.** The "Copied" bubble offers what fits the capture: One Line, Numbers,
  Markdown, Open Link, Email, Call, or Original (undo all formatting).
- **Look-alike letter fix.** OCR sometimes returns Cyrillic "і" inside an English "Hi".
  TextSnag swaps look-alike letters back in mostly-Latin text, so words stay searchable and
  code stays runnable.

## Features

- **Start Capture** with a global shortcut (default **⌥⌘5**): native macOS crosshair, text goes straight to the clipboard
- **Custom shortcuts** for Start Capture, Capture Without Line Breaks, Capture Previous Area and Toggle Additive Clipboard
- **Formatting, every part optional** (Settings → Formatting): keep line breaks or join into
  paragraphs, keep indentation, tidy bullets (●, ▪, -, * → •), fix punctuation spacing,
  straight quotes, code and table detection
- **Additive clipboard**: each capture is appended to the previous ones
- **QR codes and barcodes** are read in the same capture; optionally open their links
- **Read text aloud** after capturing
- Automatic language detection (every language macOS Vision supports)
- "Copied" bubble, optional sound, launch at login, menu bar only

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
4. Optional, for Direct Text: Settings → Formatting → **Allow…** and turn on TextSnag under
   Privacy & Security → Accessibility.

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
| `OCR.swift` | Vision text + barcode recognition, code re-reads, warm-up |
| `TextFormatter.swift` | Code/table detection, formatting options, quick-action transforms |
| `DirectText.swift` | Reads an app's own text via the Accessibility API, and checks it against OCR |
| `HotKey.swift` | Global shortcuts (Carbon `RegisterEventHotKey`, no Accessibility permission needed) |
| `Settings.swift`, `SettingsWindow.swift` | Preferences and the shortcut recorder |
| `Toast.swift` | The "Copied" bubble and its quick-action buttons |
| `scripts/make-icon.swift` | Draws the app icon and writes `Resources/AppIcon.icns` |

## License

MIT
