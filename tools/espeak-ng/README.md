# espeak-ng Development Tool

This directory contains an acquisition manifest for [espeak-ng](https://github.com/espeak-ng/espeak-ng),
an open-source speech synthesizer used during development for IPA phoneme transcription.

espeak-ng binaries and data are **not tracked** in this repository. Contributors
must run the acquisition script to populate the working directory.

## Setup

```powershell
./Fetch-EspeakNg.ps1
```

This downloads the expected Windows MSI, verifies checksums, and extracts the
distribution into this directory.

Upstream ships Windows builds as an MSI (`espeak-ng.msi` on 1.52.0), not a
win-x64 zip.

## Usage

After acquisition, the `espeak-ng.exe` binary and `espeak-ng-data/` directory
will be available locally for phonemization scripts.

## License

espeak-ng is licensed under GPL-3.0-or-later. See [LICENSE](LICENSE) for the
full text. This tool is used for development only and is not distributed as
part of any shipped product.
