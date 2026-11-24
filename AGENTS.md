# 回放尖塔 · Replay the Spire

This is the independent GPL-3.0-only Slay the Spire 2 replay mod. Keep the English and Chinese names and the player-facing description in the manifest. Read README.md, docs/development.md and docs/validation.md when resuming integration work.

Maintain single-player native recording and playback, room and decision seeking, card statistics, the forum album and sound MP4 export as one baseline. Normal play should capture events and room checkpoints. Reserve screenshots, sound capture and FFmpeg for player-requested exports. Test changes against the supported game builds. Compare actual game states, portable-file behavior and the player's saved files. Keep tests muted and route specific sound tests to a virtual output.

Use scoped Conventional Commits. Public Git history contains our source, tests, instructions and license. Keep installed game files, SDKs, decompiled sources, machine-specific paths, real profiles and gameplay media in ignored private directories. Give synthetic fixtures, native game results and subjective player acceptance distinct evidence. New modes such as practice branches or multiplayer require their own acceptance criteria.
