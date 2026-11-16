# Sentences and the speaking palette

**Sentences** (companion 2.42+) puts sentences together from words that were really said in the [voice library](../README.md#voice-library)'s recordings. The button sits beside **Voices** on the board. The window has three tabs: **Sentences** (the builder and the list), **Palette** and **Sets**.

## The rule: only real recordings

Every word you hear comes from a recording in the voice library. Soundpad Web has no speech synthesis, voice imitation or voice cloning, and never will: the only sound it makes up is silence (a gap or a pause) and a bleep.

- A word no recording has is a **missing word**. It is marked in the text and on the timeline (a pulsing marker, or a still dashed marker with "!" when the system asks for reduced motion; screen readers hear "Missing word … plays as a gap" or "as a bleep"). Choose **Gap** (the default) or **Bleep** for each one.
- For a missing word the builder may offer **similar recorded words** (spelled alike or with the same stem) and **candidate splices** (the start of one recorded word joined to the end of another). They are suggestions only: nothing is used until you press **Use "…"** or **Approve splice**, and a piece made that way stays marked as your choice. Choosing Gap or Bleep again takes it back.
- New recordings never change a saved sentence by themselves. **Find missing again** looks again for the missing words only, when you ask.

## Building a sentence

1. Press **New sentence** and type. After a short pause (about a third of a second) the companion matches the text to recorded words and phrases and shows them on the timeline. Typing never plays anything.
2. When you change the text, only the changed words are matched again; everything before and after keeps its take, voice and position.
3. **Mix** sets which voices are used (from the active performance set), **Smooth to Chaotic** (Smooth prefers the longest recorded phrases in one voice; Chaotic switches voices and uses shorter pieces), the timing and the volume. The same text and settings always give the same choice. Changing the mix chooses again only where the builder chose by itself: pieces you moved, swapped or chose stay. **Use altered takes** also allows recordings that are reversed, sped-up or effect copies.
4. **Add to sentence** (companion 2.45+): in the **Palette**, the **+** beside a word, phrase or named piece puts that exact take into the sentence you last had open, at the caret you left in its text (at the end if you did not place one). A caret inside a word or a recorded phrase puts it after that word or phrase. Nothing plays. Adds in a row go one after another. With no sentence open, the first add starts a new one; **Start a new one instead** does that on purpose.
5. Change single pieces: **Swap take** (the next take, or one picked from **Takes**, each previewable), **Shuffle voices** (the selection or the whole sentence), and **Try contrasting deliveries**, suggested for unsettling lines, which picks a take whose pace differs from the one before.

### The timeline

- Drag a piece anywhere. With Shift held it snaps to a neighbour's edge within 60 ms, or else to a 50 ms grid. Moving one piece moves nothing else. Overlapping pieces are shown striped; when they play, the later one cuts the earlier one with a short crossfade.
- For the selected piece: **Set edges** (where it starts and ends in its recording), fades, **Piece volume** (balanced with the others by default; setting it by hand turns balancing off for that piece), and pause lengths. **Add pause** inserts one.
- Effects (reverse, speed, keep-pitch speed, echo) apply to the selection, or to the whole sentence when nothing is selected.
- **Fluent joins** puts a short, even gap between pieces; **Original timing** uses the pause the words had in their recording. **Compare** previews the other one.
- **Gentle cleanup** softens noise between syllables and clicks at the edges, on copies only; preview **Original** and **Cleaned**.

Keyboard, on the timeline:

| Key | Does |
| --- | --- |
| Left / Right | Move between pieces |
| Space | Select the piece (Shift+Space adds it to the selection) |
| Ctrl-click or Cmd-click | Add a piece to the selection |
| Enter | Preview the piece |
| Alt+Left / Alt+Right | Nudge by 10 ms (100 ms with Shift); quick presses add up |
| Delete / Backspace | Remove the piece, leaving a pause of the same length to edit; focus stays on the timeline |
| Escape | Clear the selection; with nothing selected, close the window |

## Hearing it

- **Preview here** plays the sentence, or the selected pieces, in your own browser only. Nobody else hears it and it is never offered as a download.
- **Play into game** plays the sentence through Soundpad as it was at the moment you pressed it; edits made while it is being prepared are heard on the next Play. It plays from one of the exact-start slots in the companion's Temp category (see the 2.37 changelog), so it starts from its first sample. As for any sound, the newest click, Stop and Panic win, and a sentence that was overtaken never plays late. The player names it by its title or its text.
- Sentences longer than 2 minutes, as laid out, are refused before anything is put together.
- The first Play into game may wait up to 2 seconds for a quiet moment to set up the slots; if Soundpad keeps playing, Play says it is getting ready.

## Keep, versions and the board

- **Keep** renders the sentence and saves it as version 1; **Keep new version** adds the next one (up to 50 per sentence). **Restore** brings an older version back as the one you edit.
- A kept version always plays its saved audio, so later edits, corrected words or removed recordings never change it. The sentence lists the pieces whose recording has changed or gone since.
- Kept sentences are listed for everyone and found in Voices' search (type **Sentence**). **Pin to board** shows a tile on the board that plays the latest kept version into the game.
- Edits not kept yet stay with their maker: everyone else sees the latest kept version.
- Only its maker or the host can change a kept sentence, and only the host can remove one from the list. Since 2.45, removing it also deletes its rendered audio (the `v<N>-….wav` files in its own folder) that no version refers to any more. Recordings in the voice library and kept clips are never touched. Audio of sentences removed with an older companion stays on disk.

## The speaking palette

- **Palette** lists the recorded words and phrases of the voice library, the best takes of each voice, with a search that suggests words as you type. **Taps:** chooses whether a tap plays **Preview here** or **Play into game**.
- **Name…** marks part of a recording (up to 30 s) as a nonverbal sound with a short name, such as `laugh`, `gasp` or `breath`. Named pieces appear in the palette, and typing `[laugh]` in a sentence uses them.
- **Record performance** notes your own palette taps (nobody else's) with their timing, for up to 10 minutes or 400 taps. **Stop recording** saves the take as a sentence, each tap a piece at its time; **Open the take as a sentence** edits it. If the take cannot be saved, recording carries on so nothing is lost.

## Performance sets

Everyone has a **default** set and up to 31 more. A set holds voices (the palette's and the builder's voice filter), palette favourites, **templates** and (2.45+) **phrase blocks**. Each person picks their own active set. Sets refer to recordings and named pieces in the library and hold no audio. The companion keeps them, so a guest's sets follow them across reloads.

**Templates** are sentences with slots to fill in:

| Slot | Means |
| --- | --- |
| `{name}` | Words you type when you use the template |
| `{who:voice}` (2.45+) | A voice you choose when you use it. The words after the slot, up to the next voice slot, are said by that voice only ("the mix" leaves them to the sentence's voice mix) |
| `{Greeting:block}` (2.45+) | The phrase block called Greeting (any case), said in its own voice. The words after it go back to the voice before it |

**Phrase blocks** (2.45+) are words you use often, each with a name (unique in the set) and optionally the voice that says them. In **Sets → Phrase blocks** you can add, **Edit** (rename, change the words or the voice), remove, **Add to sentence** (at the caret, as above) or start a **New sentence** with one. Nothing plays.

Words a voice slot or a phrase block gave a voice stay in that voice: changing the mix, **Swap take**, **Shuffle voices**, **Find missing again** and similar words only choose among that voice's takes, and a word you type in the middle of them is looked up in that voice too. If that voice never said a word, the word stays missing and is never taken from another voice. The piece's details say "Said by … only".

## Guests

| Guests may | Needs |
| --- | --- |
| See the list, open sentences, search the palette, preview sentences and palette words, use their own sets | Nothing more than joining |
| Build sentences, name palette pieces, record performances | **Edit the voice library** |
| Keep a sentence | **Edit the voice library**, **Keep clips** and room in their Keep budget |
| Play a kept version or a pinned tile into the game | Remote playback |
| Play a palette word, or a sentence put together from words, into the game | Remote playback and **Seeking & pause** (it is part of a recording, like a region) |

The host's per-guest **Off** applies to all of these, and Panic pauses guests' sentences like any guest playback.

The host (companion 2.45+) sees each guest's unkept sentences under **Guests' sentences in progress** in the Sentences list. **Show** lists them, and one opens read-only (with **Discard …'s sentence**). **Clear …'s** removes all of that guest's unkept sentences after a second confirmation; their kept sentences stay. Guests never see one another's unkept sentences, and the host's own list stays as it was. A guest's sentence that is still being prepared when the host removes them, turns remote playback or seeking off, or presses Panic does not play.

Limits that keep room for the host:

- Guests may make up to 240 sentence changes a minute each; the host is never limited.
- The sentence list holds 2000 sentences; guests leave 400 of them, and 1000 of the 5000 named pieces, to the host. A guest's change may grow the sentences file to at most three quarters of its limit.
- A guest has one Keep at a time, and Keeps made at the same moment cannot overspend their allowance. A Keep that fails leaves no audio file behind.
- Guests' unkept sentences that nobody has touched for 14 days are removed the next time someone creates a sentence or stops a performance.
- Guest previews are made at 16 kHz and share Mimic's preview limits; palette taps are limited to 90 a minute.

## Where it is saved

In the data folder's `library\` (by default `%LOCALAPPDATA%\SoundpadWeb\library\`):

- `sentences.v1.json`: every sentence with its versions, the named pieces and everyone's performance sets (2.45 adds two optional fields, a piece's `voiceLock` and a set's `blocks`, written only when used, so older companions read the file as before; an older companion that changes sentences drops them). It is checked strictly when loaded and before every save, written safely (the previous copy is kept as `sentences.v1.bak.json`) and never saved larger than 64 MiB, the most the companion loads. If it is damaged the backup is used; if both are damaged, sentences are blocked and both files are left untouched.
- `sentences\<id>\v<N>-<hash8>.wav`: one file per kept version, written once and never changed.

`state.json` and the managed-audio journal are not changed by sentences. Going back to an older companion: see [rolling back](ROLLBACK.md).

## Not checked yet

- Nothing was listened to while this was built: the tests use the test Soundpad and stand-in recordings. How joins, balancing, cleanup, bleeps and effects sound on real voices is unmeasured, and the cleanup is a careful first version.
- Sentences have not yet been played on the real Soundpad. They use the 2.37 slot mechanism, measured then for cue starts; a sentence slot can hold up to 2 minutes, longer than slices usually were.
- Splices and "unsettling" lines are simple heuristics (a share of the word's time by its letters, and a word list).
- Matching was tested on a small library, not on thousands of recordings.

Protocol details: [shared/protocol/README.md](../shared/protocol/README.md#sentences-and-the-speaking-palette-242).
