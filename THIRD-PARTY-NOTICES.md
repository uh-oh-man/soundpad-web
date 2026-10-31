# Third-party notices

Soundpad Web is MIT licensed. Soundpad is a separate Leppsoft product and is not redistributed.

The pipe protocol implementation was developed against Leppsoft's MIT-licensed Java remote-control reference. Its copyright and permission notice is preserved in `docs/licenses/Soundpad-reference.txt`.

The browser bundle includes React, React DOM, Scheduler, Trystero and its Nostr/core packages, Zod, Lucide React and @noble/secp256k1. Their full installed license texts are collected by `npm run notices` into `web/public/THIRD-PARTY-NOTICES.txt`, distributed unchanged in the static build. Lucide includes ISC and inherited MIT notices; other listed browser packages use MIT licenses. Package versions are fixed by package-lock.json.

The standalone companion includes .NET / ASP.NET Core and System.Drawing.Common, provided under Microsoft's MIT license and applicable third-party terms. Their notices are retained under `docs/licenses/` and embedded in the executable. Run `SoundpadWeb.exe --licenses` to print them. No Leppsoft binary or audio file is shipped.

Mimic transcripts (2.5.0+) use Whisper.net 1.9.1 and its whisper.cpp / GGML native libraries, both MIT licensed (`docs/licenses/whisper-net-license.txt`, `docs/licenses/whisper-cpp-license.txt`). The native libraries are embedded in the companion and written to `%LOCALAPPDATA%\SoundpadWeb\native\` on first use. The Whisper `base.en` model is **not** distributed: the host downloads it on request from the Whisper.net GGML distribution on Hugging Face. OpenAI's Whisper model is MIT licensed (https://github.com/openai/whisper/blob/main/LICENSE).

The `mimic/` folder contains the Voice Replay Studio desktop app with its own `THIRD-PARTY.md` and `Licenses/`.

Development dependencies are not included in the distributed browser or companion. Their license metadata is available in package-lock.json and their installed packages.
