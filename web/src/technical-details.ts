/**
 * Technical details for error notices (2.45, D07): what was asked, of whom, and what came back, as plain lines
 * someone can copy into a report. A guest's copy never shows where files are on the host's PC.
 */

// A drive or network path up to the end of its last folder (folder names never hold a colon); the file name
// after it stays, since it is what the line is about. The companion hides paths the same way (BridgeDiagnostics.cs).
const PATH_FOLDERS = /(?:[A-Za-z]:\\|\\\\[^\\\s"'<>|]+\\)(?:[^\\\r\n"'<>|:]*\\)*/g;

/** Hides the folders of every Windows path in `text`: `C:\Users\Pat\Music\horn.wav` becomes `…\horn.wav`. */
export const redactPaths = (text: string) => text.replace(PATH_FOLDERS, '…\\');

export type CommandFailure = {
  op: string;
  id: string;
  /** The companion's answer, or what went wrong on the way. */
  error: string;
  role: 'host' | 'guest' | 'landing';
  companion?: string | null;
  website: string;
  at: number;
};

/** The details of a command that failed; paths are hidden on a guest's page. */
export function commandDetails(f: CommandFailure) {
  const answer = f.role === 'guest' ? redactPaths(f.error) : f.error;
  return [
    `Time: ${new Date(f.at).toISOString()}`,
    `Page: ${f.role}`,
    `Command: ${f.op}`,
    `Command id: ${f.id}`,
    `Website: ${f.website}`,
    f.companion ? `Companion: ${f.companion}` : '',
    `Answer: ${answer}`,
  ]
    .filter(Boolean)
    .join('\n');
}
