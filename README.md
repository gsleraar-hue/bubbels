# Bubbels

Put any window in a floating bubble, like chat bubbles on Android. For Windows and macOS.

Press a hotkey and the window you are working in folds into a round bubble at the edge of
the screen. Click the bubble and the window opens right beside it; click anywhere else and
it folds away again. Handy for a chat, a music player, notes or a video you want at hand
without it taking up your screen.

## Download

Grab the latest version from [Releases](../../releases/latest):

- **Windows:** `Bubbels-x.y.z-setup.exe` (installs per user, no admin rights needed), or
  `Bubbels-x.y.z-portable.exe` to run without installing.
- **macOS 13 or newer:** `Bubbels-x.y.z-mac.dmg` (Apple Silicon and Intel).

## How it works

- **Ctrl+Alt+O** (Windows) or **Control-Option-O** (Mac) puts the active window in a bubble.
  You can also pick a window from the tray / menu bar icon.
- All bubbles form **one stack** against the screen edge.
- **Click the stack**: the bubbles fan out into a column and the top window opens beside its
  bubble. Click another bubble to switch. Click the open bubble, or anywhere else, and
  everything springs back into the stack, the last window you looked at on top.
- **Drag the stack**: the other bubbles trail behind the one you hold. Let go, or flick it, and
  the stack springs to the nearest edge. Drop it on the **X** at the bottom to turn every
  bubble back into a normal window.
- In the open column you can drag a **single bubble** out; dropping it on the X releases just
  that window, at its old position and size.
- **Right-click a bubble** to open it, release it, or close the window.
- An unread count at the start of a title, like `(3) WhatsApp`, shows up as a red badge. When
  an app asks for attention, its bubble pulses.
- **Bubbles always on top** (on by default, switch it in the menu): bubbles float above every
  window. Switch it off and they behave like ordinary windows, so a full-screen video can
  cover them.
- The interface is in English, or in Dutch when your system is.

## Updates

Bubbels checks GitHub for a new version shortly after it starts and every six hours.

- **Windows (installed):** with *Update automatically* on (the default), a new version installs
  itself silently, but only while no window is in a bubble, so it never pulls a window out from
  under you. Otherwise the menu shows *Update to x.y.z*. The portable exe only points you to
  the download.
- **macOS:** Bubbels asks before updating (*Update now*, *Later*, *Skip this version*) and then
  replaces itself. macOS may ask for Accessibility access again after an update.
- Either way: *Check for updates* in the menu.

## Windows notes

- Windows of programs that run as administrator cannot be bubbled.
- A bubbled window is hidden. Bubbels gives every window back when it quits, when you sign out
  and when you uninstall; if it ever stops unexpectedly, the next start brings hidden windows
  back (`%APPDATA%\Bubbels\bubbels.state`).
- Log file: `%APPDATA%\Bubbels\bubbels.log`.

## macOS notes

- Bubbels needs **Accessibility** access to move and hide other apps' windows. It asks on first
  launch; you can also grant it in System Settings > Privacy & Security > Accessibility.
- The app is not notarised (that needs a paid Apple developer account). On first launch,
  right-click Bubbels.app and choose **Open**. After an update you may have to switch
  Accessibility access off and on again.
- macOS does not let one app keep another app's window above the rest, so an opened window is
  simply brought to the front. When an app has a single window, Bubbels hides the app;
  otherwise it minimises the window.

## Building

**Windows:** run `build.cmd`. It uses the `csc.exe` that ships with the .NET Framework on every
Windows machine: no SDK, no packages. Output: `bin\Bubbels.exe` and `bin\Bubbels-setup.exe`.
`Bubbels.exe --selftest <hwnd> [hwnd]` walks windows through bubble, open, switch, fold and
release, and writes each state to the log. `Bubbels.exe --check-update` logs what the updater
sees. When you release, bump the version in `src/AssemblyInfo.cs` and `setup/Installer.cs`; the
workflow refuses a tag that does not match.

**macOS:** run `mac/build.sh 1.0.0` with the Xcode command line tools installed. Output in
`dist/`.

GitHub Actions builds both on every push and publishes a release for every `v*` tag.

## Licence

MIT
