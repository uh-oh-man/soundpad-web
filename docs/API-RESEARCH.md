# Verified vendor protocol and implementation decisions

Research performed 2026-10-29 from primary sources:

- [Official Soundpad manual](https://www.leppsoft.com/soundpad/help/manual/tutorial/rc/)
- [Official remote-control reference](https://www.leppsoft.com/soundpad/files/rc/SoundpadRemoteControl.java) (RC 1.1.2)
- [Vendor-linked reference implementations](https://www.leppsoft.com/soundpad/en/rc/)
- [TypeScript implementation XML parsing](https://github.com/Seblor/soundpad.js/blob/main/src/soundpad.ts)
- [.NET connector XML models and message-mode transport](https://github.com/medokin/soundpad-connector)
- [Trystero 0.26 API](https://github.com/dmotz/trystero), [connection and access documentation](https://trystero.dev/docs/connections/)
- [Chrome local-network permission changes](https://developer.chrome.com/blog/local-network-access)

The official Java implementation opens `\\.\pipe\sp_remote_control`, writes commands without newline, serializes requests and inserts a short spacing delay. It returns UTF-8 response text. `R-200` means success. `GetSoundlist()` is the All sounds list, regardless of the selected category. `DoPlaySound(index)` indexes that global list and respects the previous Soundpad playback mode. Category-specific playback uses a different position; this app never sends those positions as global indexes. `GetCategories(true, true)` returns nested category XML including base64 PNG icon attributes. Unsupported categories produce an explicit update-required state rather than a fabricated flat tree.

XML is case-sensitive: `Soundlist/Sound` and `Categories/Category`, with direct nested `Category` and `Sound` elements. Sound attributes include `index`, `url`, `artist`, `title`, `duration`, `addedOn`; category attributes include `index`, `name`, `type`, `hidden`, `icon`. Fixtures are **schema-derived**, not real captures from this computer. All returned categories, including hidden ones, stay in vendor order. The website's All sounds button is a global aggregate; native categories retain their own contents and nested navigation.

The parser forbids DTDs/external entities, caps XML size/depth, checks indexes and category URL references, and publishes no paths. Sound IDs are HMAC-SHA256 over normalized file URL plus duplicate occurrence, using a local random persisted key. Reordering unique paths preserves identities. Exact duplicate URLs cannot be distinguished perfectly; occurrence numbering may move icons among otherwise identical duplicates. Renaming/moving files changes identity. Titles are never used as identity. Native category icons and custom icons are validated and re-encoded as 96-pixel PNGs; image metadata is removed.

Before every play the bridge refreshes both metadata snapshots and rejects an obsolete version. There is still an unavoidable narrow race if a user reorders Soundpad between the last query and the command: the vendor API offers no transactional play-by-persistent-ID. This is disclosed rather than claiming an impossible atomic guarantee.

The .NET pipe uses message boundaries, not newline delimiters, time-based reads, or one-read assumptions. A single persistent pipe is serialized and yields between requests. Broken pipes dispose/reconnect on the next poll. No Soundpad installation-path discovery, startup, configuration changes, audio-device modifications, or raw API proxy is needed.
