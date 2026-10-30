import { blankWorkspace, workspaceSchema, type Workspace } from './workspace';
export type Organization = Pick<Workspace, 'folders' | 'favorites' | 'hidden' | 'order'>;
export type SharedLayout = { owner: string; name: string; organization: Organization };
export function organization(w: Workspace): Organization {
  return { folders: w.folders, favorites: w.favorites, hidden: w.hidden, order: w.order };
}
export function parseOrganization(raw: string, hostId: string): Organization {
  if (raw.length > 2 * 1024 * 1024) throw Error('Shared layout exceeds 2 MiB.');
  const value = JSON.parse(raw);
  if (
    !value ||
    typeof value !== 'object' ||
    Object.keys(value).some((k) => !['folders', 'favorites', 'hidden', 'order'].includes(k))
  )
    throw Error('Invalid shared organization.');
  return organization(workspaceSchema.parse({ ...blankWorkspace(hostId), ...value }));
}
