/**
 * Honest labels for the example session (Stage G, spec 11.5). `SimulatedBadge` marks something the
 * example only pretends (a guest, a voice, a guest's selection); `HostOnlyTag` marks a control that
 * works only with the companion on the host's PC. Both are text, never colour alone, so screen
 * readers and forced colours read them too.
 */
export function SimulatedBadge({ className = '' }: { className?: string }) {
  return <span className={`simulated-badge ${className}`.trim()}>Simulated</span>;
}

export function HostOnlyTag({ className = '', id }: { className?: string; id?: string }) {
  return (
    <span id={id} className={`host-only-tag ${className}`.trim()}>
      Host’s PC only
    </span>
  );
}
