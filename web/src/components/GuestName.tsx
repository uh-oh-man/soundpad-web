import { useState } from 'react';
import { controller } from '../controller';
export function randomGamerName() {
  const adjectives = [
    'Cosmic',
    'Pixel',
    'Sleepy',
    'Lucky',
    'Funky',
    'Turbo',
    'Velvet',
    'Electric',
    'Sneaky',
    'Solar',
    'Mighty',
    'Chill',
    'Golden',
    'Bouncy',
    'Neon',
    'Wobbly',
    'Fuzzy',
    'Lunar',
    'Spicy',
    'Wicked',
  ];
  const animals = [
    'Otter',
    'Badger',
    'Gecko',
    'Panda',
    'Raven',
    'Mango',
    'Wombat',
    'Falcon',
    'Noodle',
    'Fox',
    'Koala',
    'Llama',
    'Penguin',
    'Dragon',
    'Pickle',
    'Mantis',
    'Turtle',
    'Corgi',
    'Axolotl',
    'Capybara',
  ];
  const pick = (size: number) => {
    const value = new Uint32Array(1);
    crypto.getRandomValues(value);
    return value[0] % size;
  };
  return `${adjectives[pick(adjectives.length)]}${animals[pick(animals.length)]}${100 + pick(900)}`;
}
export function GuestName({ initial }: { initial: string }) {
  const [name, setName] = useState(initial === 'Guest' ? '' : initial);
  return (
    <div className="scrim">
      <form
        className="dialog guest-name-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="guest-name-title"
        onSubmit={(e) => {
          e.preventDefault();
          if (name.trim()) controller.confirmName(name);
        }}
        onKeyDown={(e) => {
          if (e.key !== 'Tab') return;
          const nodes = e.currentTarget.querySelectorAll<HTMLElement>(
            'input,button:not(:disabled)',
          );
          const first = nodes[0],
            last = nodes[nodes.length - 1];
          if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last.focus();
          } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
          }
        }}
      >
        <h2 id="guest-name-title">What’s your gamer name?</h2>
        <p>Your friends will see this name when you play a sound.</p>
        <input
          autoFocus
          aria-label="Gamer name"
          value={name}
          maxLength={40}
          onChange={(e) => setName(e.target.value)}
          autoComplete="nickname"
        />
        <div className="editor-actions">
          <button type="button" onClick={() => setName(randomGamerName())}>
            Random
          </button>
          <button className="primary" type="submit" disabled={!name.trim()}>
            Done
          </button>
        </div>
      </form>
    </div>
  );
}
