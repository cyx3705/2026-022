# FFmpeg runtime notice

- Component: FFmpeg 8.0.1 essentials build for Windows x64
- Upstream: https://ffmpeg.org/
- Binary distributor: https://www.gyan.dev/ffmpeg/builds/
- Frozen archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.0.1-essentials_build.zip
- Archive SHA-256: `E2AAEAA0FDBC397D4794828086424D4AAA2102CEF1FB6874F6FFD29C0B88B673`
- Bundled `ffmpeg.exe` SHA-256: `5AF82A0D4FE2B9EAE211B967332EA97EDFC51C6B328CA35B827E73EAC560DC0D`
- Build identity: `ffmpeg version 8.0.1-essentials_build-www.gyan.dev`
- Relevant configuration: `--enable-gpl --enable-version3 --enable-static --enable-libx264`
- License: GNU General Public License version 3 or later. The distributor's original license text is bundled beside this notice as `LICENSE`; see also https://ffmpeg.org/legal.html.

WBall invokes this unmodified executable as a separate process and streams raw BGRA frames through standard input. The V3.6 package uses `libx264` to produce H.264 video in an MP4 container. When redistributing WBall with this binary, preserve this notice and comply with the FFmpeg build's GPLv3 source and license obligations.
