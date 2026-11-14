import { Users } from 'lucide-react';
import { controller } from '../controller';
import { MAX_PLACES, places, type PermissionKey } from '../network/policy';
import { supports } from '../version';
import { LobbyRequests } from './LobbyRequests';
import { GuestUsage } from './StoragePanel';

type View = ReturnType<typeof controller.getSnapshot>;

/** The per-guest choices (2.37), in the order of the session-wide switches above them. */
export const GUEST_PERMISSIONS: {
  key: PermissionKey;
  label: string;
  v2?: boolean;
  feature?: Parameters<typeof supports>[1];
}[] = [
  { key: 'remotePlayback', label: 'Remote playback' },
  { key: 'guestCounter', label: 'Kick counter' },
  { key: 'guestSpotify', label: 'Spotify controls', v2: true },
  { key: 'guestSeeking', label: 'Seeking & pause', v2: true },
  { key: 'guestUploads', label: 'Session uploads' },
  { key: 'guestTransforms', label: 'Reverse generation' },
  { key: 'guestMimic', label: 'Mimic', v2: true, feature: 'mimic' },
  { key: 'guestKeep', label: 'Keep clips', v2: true, feature: 'drafts' },
  { key: 'guestLibraryEdit', label: 'Edit the voice library', v2: true, feature: 'voiceLibrary' },
];

/**
 * Who is in the session (2.37): admission, guest places, waiting guests, each guest's connection and
 * own permissions, and guests the host turned away. Changes are made in the page that runs the
 * session; a view shows the same and says where to change it.
 */
export function SessionAccess({ view }: { view: View }) {
  const { policy, state } = view;
  const v2 = (state.protocol ?? 1) >= 2;
  const total = places(policy);
  const choices = GUEST_PERMISSIONS.filter(
    (p) => (v2 || !p.v2) && (!p.feature || supports(state.companionVersion, p.feature)),
  );
  return (
    <section className="session-access" aria-label="Who can join">
      <div className="setting-row">
        <div>
          <strong>Admission</strong>
          <p>Ask me first holds new guests until you let them in.</p>
        </div>
        <select
          aria-label="Admission"
          value={policy.admission}
          onChange={(e) => controller.setAdmission(e.target.value as 'open' | 'ask')}
        >
          <option value="open">Let guests in at once</option>
          <option value="ask">Ask me first</option>
        </select>
      </div>
      <div className="setting-row">
        <div>
          <strong>Guest places</strong>
          <p>
            {policy.extra
              ? `Plus ${policy.extra} extra place${policy.extra === 1 ? '' : 's'} granted this session.`
              : 'When it is full, a new guest can ask you for an extra place.'}
          </p>
        </div>
        <select
          aria-label="Guest places"
          value={policy.capacity}
          onChange={(e) => controller.setCapacity(Number(e.target.value))}
        >
          {Array.from({ length: MAX_PLACES }, (_, i) => i + 1).map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
      </div>
      <LobbyRequests requests={view.requests} />
      <h3>
        Connected guests{' '}
        <small>
          {view.guests.length} / {total}
        </small>
      </h3>
      {view.guests.length ? (
        view.guests.map((id, i) => {
          const person = view.people[id];
          const name = person?.name ?? `Guest ${i + 1}`;
          const own = (person && policy.overrides[person.id]) || {};
          // An override can only turn a permission off (2.37 review); an "On" kept from before counts as
          // "Same as everyone", which is also what it does.
          const custom = Object.values(own).filter((value) => value === false).length;
          return (
            <div className="guest-row" key={id}>
              <span>
                <Users size={17} /> {name} <small>{id.slice(0, 8)}</small>{' '}
                <span className={`link-kind ${view.links[id] ?? 'direct'}`}>
                  {view.links[id] === 'relay' ? 'Relayed' : 'Direct'}
                </span>{' '}
                <GuestUsage participantId={person?.id} />
              </span>
              <details className="guest-permissions">
                <summary aria-label={`Permissions for ${name}`}>
                  Permissions{custom ? ` (${custom} own)` : ''}
                </summary>
                {choices.map(({ key, label }) => (
                  <label key={key}>
                    {label}
                    <select
                      aria-label={`${label} for ${name}`}
                      value={own[key] === false ? 'off' : 'same'}
                      onChange={(e) =>
                        controller.setGuestPermission(
                          id,
                          key,
                          e.target.value === 'off' ? false : undefined,
                        )
                      }
                    >
                      <option value="same">Same as everyone</option>
                      <option value="off">Off</option>
                    </select>
                  </label>
                ))}
              </details>
              <button onClick={() => controller.disconnect(id)}>Disconnect</button>
            </div>
          );
        })
      ) : (
        <p className="muted">Share your invitation to bring friends in.</p>
      )}
      {!!policy.turnedAway.length && (
        <>
          <h3>Turned away</h3>
          {policy.turnedAway.map((person) => (
            <div className="guest-row" key={person.id}>
              <span>
                <Users size={17} /> {person.name}
              </span>
              <button
                aria-label={`Allow ${person.name} again`}
                onClick={() => controller.letIn(person.id)}
              >
                Allow again
              </button>
            </div>
          ))}
        </>
      )}
    </section>
  );
}
