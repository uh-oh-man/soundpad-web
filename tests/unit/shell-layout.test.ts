import { expect, it } from 'vitest';
import {
  EDITOR_DEFAULT,
  EDITOR_MAX,
  EDITOR_MIN,
  clampEditorWidth,
  editorMaxWidth,
  splitterKey,
} from '../../web/src/components/shell/Splitter';
import { navPlacement } from '../../web/src/components/shell/SectionNav';
import { isEditorRequest } from '../../web/src/shell';

it('keeps the board at least 480 px wide and the panel between 360 and 900 px', () => {
  // 1440 with the sidebar: 1440 - 234 - 480 - 8.
  expect(editorMaxWidth(1440, 234)).toBe(718);
  // The VR window with the sidebar folded to its rail: 1100 - 64 - 488.
  expect(editorMaxWidth(1100, 64)).toBe(548);
  expect(editorMaxWidth(4000, 234)).toBe(EDITOR_MAX);
  // Never narrower than the minimum, even when the window is too small for both.
  expect(editorMaxWidth(900, 234)).toBe(EDITOR_MIN);
  expect(clampEditorWidth(2000, 1440, 234)).toBe(718);
  expect(clampEditorWidth(100, 1440, 234)).toBe(EDITOR_MIN);
  expect(clampEditorWidth(Number.NaN, 1440, 234)).toBe(EDITOR_DEFAULT);
  expect(clampEditorWidth(512.4, 1440, 234)).toBe(512);
});

it('moves the splitter by key: arrows by 16 px (64 with Shift), Home, End, and Enter toggles', () => {
  const max = 700;
  expect(splitterKey('ArrowLeft', false, 480, max, 480)).toBe(496);
  expect(splitterKey('ArrowRight', false, 480, max, 480)).toBe(464);
  expect(splitterKey('ArrowLeft', true, 480, max, 480)).toBe(544);
  expect(splitterKey('ArrowRight', true, 400, max, 480)).toBe(EDITOR_MIN);
  expect(splitterKey('ArrowLeft', true, 680, max, 480)).toBe(max);
  expect(splitterKey('Home', false, 600, max, 480)).toBe(EDITOR_MIN);
  expect(splitterKey('End', false, 600, max, 480)).toBe(max);
  expect(splitterKey('Enter', false, 600, max, 480)).toBe(EDITOR_MIN);
  expect(splitterKey('Enter', false, EDITOR_MIN, max, 600)).toBe(600);
  expect(splitterKey('a', false, 600, max, 480)).toBeNull();
});

it('mounts the section nav in the topbar for top tabs or a narrow screen, else in the sidebar', () => {
  expect(navPlacement('sidebar', false)).toBe('sidebar');
  expect(navPlacement('tabs', false)).toBe('topbar');
  expect(navPlacement('sidebar', true)).toBe('topbar');
});

it('accepts only well-formed editor requests', () => {
  expect(isEditorRequest({ kind: 'mimic' })).toBe(true);
  expect(isEditorRequest({ kind: 'temp-edit', sourceId: 'a1' })).toBe(true);
  expect(isEditorRequest({ kind: 'temp-edit' })).toBe(false);
  expect(isEditorRequest({ kind: 'sound-edit', soundId: '3', focus: 'statistics' })).toBe(true);
  expect(isEditorRequest({ kind: 'sound-edit', soundId: '3', focus: 'other' })).toBe(false);
  expect(isEditorRequest({ kind: 'nope' })).toBe(false);
  expect(isEditorRequest(null)).toBe(false);
});
