import { UserPlus } from 'lucide-react';
import { controller } from '../controller';
import type { LobbyRequest } from '../network/session';

/**
 * Guests waiting for the host (2.37): a list that stays until the host decides or the request lapses
 * after two minutes, never a notice that disappears. A view of another page's session shows it too,
 * and its buttons say where to decide (only the page that runs the session can let anyone in).
 */
export function LobbyRequests({ requests }: { requests: LobbyRequest[] }) {
  if (!requests.length) return null;
  return (
    <ul className="lobby-requests" aria-label="Guests waiting to join">
      {requests.map((request) => (
        <li key={request.id}>
          <UserPlus size={16} aria-hidden="true" />
          <span>
            <strong>{request.name}</strong>{' '}
            {request.kind === 'extra' ? 'asks for an extra place' : 'wants to join'}
          </span>
          <button
            className="primary"
            aria-label={`Let in ${request.name}`}
            onClick={() => controller.letIn(request.id)}
          >
            Let in
          </button>
          <button
            aria-label={`Turn away ${request.name}`}
            onClick={() => controller.turnAway(request.id)}
          >
            Turn away
          </button>
        </li>
      ))}
    </ul>
  );
}
