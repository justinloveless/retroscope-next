import { test } from 'node:test';
import assert from 'node:assert/strict';
import { jiraMessage } from '../../../web/src/core/jira-errors.js';

test('a code gets its shared sentence, and a screen can word a code for what it asked Jira for', () => {
  assert.equal(jiraMessage({ code: 'jira_not_found' }), 'Jira could not find that issue.');
  assert.equal(jiraMessage('jira_not_found'), 'Jira could not find that issue.');
  const overrides = { jira_not_found: 'Jira could not find that board.' };
  assert.equal(jiraMessage({ code: 'jira_not_found' }, overrides), 'Jira could not find that board.');
  assert.equal(jiraMessage({ code: 'jira_rate_limited' }, overrides), 'Jira is busy. Try again in a moment.');
});

test('a validation error shows its first field message, and an unknown code its detail or a fallback', () => {
  assert.equal(jiraMessage({ code: 'jira_validation', fields: { points: 'Points must be a number.' } }), 'Points must be a number.');
  assert.equal(jiraMessage({ code: 'mystery', detail: 'Something specific.' }), 'Something specific.');
  assert.equal(jiraMessage(new Error('boom')), 'Something went wrong with Jira.');
});
