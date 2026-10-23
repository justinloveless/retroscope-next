// Fake Jira proxy (`/api/teams/{id}/jira/...`, DESIGN §f) backed by an in-memory copy of a fixture
// (fixtures/jira.json by default). It answers with the problem codes of the §f error table and keeps
// writes, so a drawer edit, a points submit or a create is visible to the next read. Response shapes
// are this fake's reading of §f until U26 lands; a spec that needs another shape overrides the route
// with fake.route().
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { DIALECT_JIRA, DIALECT_JIRA_DESCRIPTION } from '../../web/src/markup/doc.js';
import { parse } from '../../web/src/markup/parse.js';
import { adfToDoc, docToAdf, EDIT_LIMITS, EMPTY_DOC, hasTokens, mediaTable, restoreTokens, tokenRefs } from './adf.js';
import { json, problem } from './http.js';

const here = dirname(fileURLToPath(import.meta.url));
export const DEFAULT_JIRA_FIXTURE = join(here, 'fixtures/jira.json');
// The real projection of the fixture issues that hold tokens (WebFakeAdfTests writes it).
const EDIT_VIEWS = JSON.parse(readFileSync(join(here, 'edit-views.gen.json'), 'utf8')).issues;

// 1×1 slate-grey PNG served for every attachment: opaque, so an embedded image shows where it sits.
const PIXEL = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR42mM4ffUhAATwAoLs9l8mAAAAAElFTkSuQmCC',
  'base64',
);

const BROWSE_STATUSES = new Set(['new', 'indeterminate', 'done']);
const COMMENT_PAGE = 20;

function descriptionDoc(issue) {
  // A rich save replaces the markup with ADF.
  if (issue.description === null && issue.adf) {
    return adfToDoc(issue.adf, issue.attachments);
  }
  const doc = parse(issue.description ?? '', { dialect: DIALECT_JIRA_DESCRIPTION });
  for (const node of doc.c) {
    if (node.t === 'island') {
      node.c = issue.islands?.[node.id] ? [issue.islands[node.id]] : [];
    }
  }
  return doc;
}

// A clone keeps the source's description form: markup with islands, or the ADF a rich save left.
function clonedMarkup(source) {
  return source.description === null ? null : `${source.description}\n\nCloned from ${source.key}`;
}

const canonical = (v) => (Array.isArray(v) ? v.map(canonical)
  : v && typeof v === 'object' ? Object.fromEntries(Object.keys(v).sort().map((k) => [k, canonical(v[k])])) : v);
const sameAdf = (text, adf) => text !== undefined && JSON.stringify(canonical(JSON.parse(text))) === JSON.stringify(canonical(adf));

function withFooter(adf, key) {
  return { ...adf, content: [...adf.content, footer(`Cloned from ${key}`)] };
}

function footer(text) {
  return { type: 'paragraph', content: [{ type: 'text', text }] };
}

function commentView(comment) {
  return {
    id: comment.id,
    author: comment.author,
    created: comment.created,
    // bodyDoc stands in for a comment whose ADF carries display-only kinds that markup cannot write.
    body: comment.bodyDoc ?? parse(comment.body, { dialect: DIALECT_JIRA }),
  };
}

export function installJira(server, { teamId, fixture = DEFAULT_JIRA_FIXTURE, configured = true } = {}) {
  if (!teamId) {
    throw new Error('fake.jira({teamId}) needs the team id the specs browse under');
  }
  const data = structuredClone(typeof fixture === 'string' ? JSON.parse(readFileSync(fixture, 'utf8')) : fixture);
  const issues = new Map(data.issues.map((issue) => [issue.key, { comments: [], attachments: [], labels: [], ...issue }]));
  const failures = [];
  const calls = [];
  let nextKey = Math.max(0, ...data.issues.map((i) => Number(i.key.split('-')[1]))) + 1;
  let nextId = 90000;
  let clock = Date.parse('2026-10-20T00:00:00.000Z');

  const control = {
    config: { ...data.config, configured },
    issues,
    sprints: data.sprints,
    calls,
    // The originals a save of a golden issue restores (edit-views.gen.json), for byte-for-byte checks.
    originals: (key) => EDIT_VIEWS[key]?.originals ?? null,
    // The next `count` proxy requests answer with this problem, e.g. problem(503, 'jira_unavailable').
    failNext(response, count = 1) {
      for (let i = 0; i < count; i++) {
        failures.push(response);
      }
    },
    // Simulates someone editing the issue in Jira: bumps `updated`, so a pending description write
    // with the old descriptionBase is refused as stale.
    touch(key, changes = {}) {
      Object.assign(requireIssue(key), changes, { updated: tick() });
    },
  };

  // A golden applies to the fixture issue it was generated from: its tokens restored give back the issue's ADF. A
  // clone of such an issue keeps its tokens, so it shares the source's originals.
  const goldens = new Set(data.issues.filter((i) => EDIT_VIEWS[i.key] && sameAdf(restoreTokens(EDIT_VIEWS[i.key].adf, EDIT_VIEWS[i.key].originals).text, i.adf)).map((i) => i.key));
  const goldenOf = (issue) => (goldens.has(issue.goldenKey ?? issue.key) ? EDIT_VIEWS[issue.goldenKey ?? issue.key] : null);

  function tick() {
    clock += 60_000;
    return new Date(clock).toISOString();
  }

  function requireIssue(key) {
    const issue = issues.get(key);
    if (!issue) {
      throw new Error(`fake Jira has no issue ${key}`);
    }
    return issue;
  }

  const sprintOf = (issue) => data.sprints.find((s) => s.id === issue.sprintId) ?? null;
  const parentSummary = (issue) => (issue.parentKey ? issues.get(issue.parentKey)?.summary ?? null : null);

  function row(issue) {
    return {
      key: issue.key,
      summary: issue.summary,
      issueType: issue.issueType,
      status: issue.status,
      statusCategory: issue.statusCategory,
      points: issue.points ?? null,
      parentKey: issue.parentKey ?? null,
      parentSummary: parentSummary(issue),
      roundState: null,
    };
  }

  function detail(issue) {
    const sprint = sprintOf(issue);
    const doc = descriptionDoc(issue);
    return {
      key: issue.key,
      updated: issue.updated,
      fields: {
        summary: issue.summary,
        status: issue.status,
        statusCategory: issue.statusCategory,
        priority: issue.priority ?? null,
        issueType: issue.issueType,
        assignee: data.users.find((u) => u.accountId === issue.assignee) ?? null,
        reporter: data.users[0] ?? null,
        parent: issue.parentKey ? { key: issue.parentKey, summary: parentSummary(issue) } : null,
        labels: issue.labels,
        points: issue.points ?? null,
        sprint: sprint ? { id: sprint.id, name: sprint.name, state: sprint.state } : null,
        created: issue.created ?? issue.updated,
        project: issue.key.split('-')[0],
      },
      description: doc,
      islandIds: doc.c.filter((n) => n.t === 'island').map((n) => n.id),
      comments: issue.comments.slice(0, COMMENT_PAGE).map(commentView),
      commentTotal: issue.comments.length,
      attachments: issue.attachments,
      links: issue.links ?? [],
    };
  }

  // GET issues/:key/description (UX10): the issue's ADF as the rich editor loads it. An issue with a golden serves
  // its projected form; after a rich save, the save's own tokenised ADF stands in for projecting Jira's copy again.
  function editView(issue) {
    const golden = goldenOf(issue);
    if (golden && issue.adf !== undefined) {
      const adf = issue.tokenAdf ?? golden.adf;
      const refs = tokenRefs(adf);
      return {
        key: issue.key,
        updated: issue.updated,
        editable: true,
        adf,
        media: golden.media,
        opaque: golden.opaque.filter((part) => refs.has(part.ref)),
        marks: golden.marks.filter((mark) => refs.has(mark.ref)),
        limits: golden.limits,
      };
    }
    const adf = issue.adf ?? docToAdf(descriptionDoc(issue));
    return {
      key: issue.key,
      updated: issue.updated,
      editable: true,
      adf,
      media: mediaTable(adf, issue.attachments),
      opaque: [],
      marks: [],
      limits: EDIT_LIMITS,
    };
  }

  // PATCH descriptionAdf: the same precondition and refusals as the server; null clears the description. Tokens are
  // restored from the golden's originals; an issue without one holds no tokens, so a save naming one is stale.
  function writeRich(issue, body) {
    if (body.description !== undefined) {
      return problem(400, 'invalid', { fields: { descriptionAdf: 'conflict' } });
    }
    if (typeof body.descriptionBase !== 'string' || body.descriptionBase === '') {
      return problem(400, 'invalid', { fields: { descriptionBase: 'required' } });
    }
    const adf = body.descriptionAdf ?? EMPTY_DOC;
    if (adf?.type !== 'doc' || !Array.isArray(adf.content)) {
      return problem(400, 'invalid', { fields: { descriptionAdf: 'not_doc' } });
    }
    const stale = () => problem(409, 'stale', { current: { updated: issue.updated } });
    if (body.descriptionBase !== issue.updated) {
      return stale();
    }
    const golden = goldenOf(issue);
    if (!golden && hasTokens(adf)) {
      return stale();
    }
    const restored = restoreTokens(adf, golden?.originals ?? {});
    if (restored.stale) {
      return stale();
    }
    if (restored.code) {
      return problem(400, 'invalid', { fields: { descriptionAdf: restored.code, 'descriptionAdf.at': restored.at } });
    }
    issue.adfText = restored.text;
    issue.adf = JSON.parse(restored.text);
    issue.tokenAdf = golden ? structuredClone(adf) : undefined;
    issue.description = null;
    issue.islands = undefined;
    return null;
  }

  function browse(query) {
    const text = (query.get('q') ?? '').trim().toLowerCase();
    const status = query.get('status');
    const points = query.get('points');
    const matches = (issue) => {
      if (issue.issueType === 'Epic') {
        return false;
      }
      if (text && !issue.key.toLowerCase().includes(text) && !issue.summary.toLowerCase().includes(text)) {
        return false;
      }
      if (status && BROWSE_STATUSES.has(status) && issue.statusCategory !== status) {
        return false;
      }
      if (points === 'pointed' && issue.points == null) {
        return false;
      }
      return !(points === 'unpointed' && issue.points != null);
    };
    const all = [...issues.values()].filter(matches);
    const buckets = data.sprints.map((sprint) => ({
      id: String(sprint.id),
      kind: 'sprint',
      name: sprint.name,
      state: sprint.state,
      startDate: sprint.startDate,
      issues: all.filter((i) => i.sprintId === sprint.id).map(row),
    }));
    buckets.push({ id: 'backlog', kind: 'backlog', name: 'Backlog', state: null, startDate: null, issues: all.filter((i) => i.sprintId == null).map(row) });
    return { buckets: buckets.filter((b) => b.issues.length > 0), partial: false };
  }

  const handlers = {
    'GET /': () => json(control.config),
    'GET /browse': ({ query }) => json(browse(query)),
    'GET /sprints': () => json({ sprints: data.sprints }),
    'POST /meta': ({ body }) => {
      const keys = Array.isArray(body?.keys) ? body.keys : null;
      if (!keys || keys.length > 100) {
        return problem(400, 'invalid');
      }
      const items = keys.filter((k) => issues.has(k)).map((k) => {
        const { roundState, statusCategory, ...meta } = row(issues.get(k));
        return meta;
      });
      return json({ items });
    },
    'POST /issues': ({ body }) => {
      if (typeof body?.summary !== 'string' || body.summary.trim() === '') {
        return problem(422, 'jira_validation', { fields: { summary: 'Summary is required.' } });
      }
      const source = body.cloneFrom ? issues.get(body.cloneFrom) : null;
      if (body.cloneFrom && !source) {
        return problem(404, 'jira_not_found');
      }
      const project = body.projectKey ?? control.config.projectKey;
      const issue = {
        key: `${project}-${nextKey++}`,
        summary: body.summary,
        issueType: source?.issueType ?? 'Task',
        status: 'To Do',
        statusCategory: 'new',
        priority: source?.priority ?? 'Medium',
        points: null,
        sprintId: null,
        description: source ? clonedMarkup(source) : body.description ?? '',
        islands: source?.islands,
        adf: source?.adf ? withFooter(source.adf, source.key) : undefined,
        goldenKey: source && goldenOf(source) ? source.goldenKey ?? source.key : undefined,
        tokenAdf: source && goldenOf(source) ? withFooter(source.tokenAdf ?? goldenOf(source).adf, source.key) : undefined,
        comments: [],
        attachments: [],
        labels: [],
        updated: tick(),
      };
      issues.set(issue.key, issue);
      return json({ key: issue.key }, 201);
    },
    'GET /issues/:key': ({ issue }) => json(detail(issue)),
    'GET /issues/:key/description': ({ issue }) => json(editView(issue)),
    'PATCH /issues/:key': ({ issue, body }) => {
      if (body?.descriptionAdf !== undefined) {
        const refused = writeRich(issue, body);
        if (refused) {
          return refused;
        }
      } else if (body?.description !== undefined) {
        if (body.descriptionBase !== issue.updated) {
          return problem(409, 'stale', { current: { updated: issue.updated } });
        }
        const known = new Set(Object.keys(issue.islands ?? {}));
        const refs = parse(body.description, { dialect: DIALECT_JIRA_DESCRIPTION }).c.filter((n) => n.t === 'island');
        if (refs.some((n) => !known.has(n.id))) {
          return problem(409, 'stale', { current: { updated: issue.updated } });
        }
        issue.description = body.description;
        // The markup now holds the description; ADF is built from it on the next edit view.
        issue.adf = undefined;
        issue.tokenAdf = undefined;
      }
      if (body?.summary !== undefined) {
        issue.summary = body.summary;
      }
      if (body?.assigneeAccountId !== undefined) {
        issue.assignee = body.assigneeAccountId;
      }
      if (body?.priorityId !== undefined) {
        issue.priority = data.priorities.find((p) => p.id === body.priorityId)?.name ?? issue.priority;
      }
      if (body?.issueTypeId !== undefined) {
        issue.issueType = data.issueTypes.find((t) => t.id === body.issueTypeId)?.name ?? issue.issueType;
      }
      issue.updated = tick();
      return json(detail(issue));
    },
    'POST /issues/:key/transition': ({ issue, body }) => {
      const transition = data.transitions.find((t) => t.id === body?.transitionId);
      if (!transition) {
        return problem(422, 'jira_validation', { fields: { transitionId: 'Unknown transition.' } });
      }
      issue.status = transition.to;
      issue.statusCategory = transition.statusCategory;
      issue.updated = tick();
      return { status: 204 };
    },
    'PUT /issues/:key/points': ({ issue, body }) => {
      const points = body?.points;
      if (points !== null && !(typeof points === 'number' && points >= 0 && points <= 1000 && Number.isInteger(points * 2))) {
        return problem(422, 'jira_validation', { fields: { points: 'Points must be 0 to 1000 in steps of 0.5.' } });
      }
      issue.points = points;
      issue.updated = tick();
      return json({ key: issue.key, points });
    },
    'PUT /issues/:key/sprint': ({ issue, body }) => {
      const sprintId = body?.sprintId ?? null;
      if (sprintId !== null && !data.sprints.some((s) => s.id === sprintId)) {
        return problem(404, 'jira_not_found');
      }
      issue.sprintId = sprintId;
      issue.updated = tick();
      return { status: 204 };
    },
    'GET /issues/:key/options': () => json({
      issueTypes: data.issueTypes,
      priorities: data.priorities,
      linkTypes: data.linkTypes,
      transitions: data.transitions.map(({ id, name, to }) => ({ id, name, to })),
    }),
    'GET /issues/:key/assignable': ({ query }) => {
      const q = (query.get('q') ?? '').toLowerCase();
      return json({ users: data.users.filter((u) => u.displayName.toLowerCase().includes(q)) });
    },
    'GET /issues/:key/comments': ({ issue, query }) => {
      const startAt = Math.max(0, Number.parseInt(query.get('startAt') ?? '0', 10) || 0);
      return json({
        startAt,
        total: issue.comments.length,
        comments: issue.comments.slice(startAt, startAt + COMMENT_PAGE).map(commentView),
      });
    },
    'POST /issues/:key/comments': ({ issue, body }) => {
      if (typeof body?.body !== 'string' || body.body.trim() === '') {
        return problem(422, 'jira_validation', { fields: { body: 'Comment is empty.' } });
      }
      const comment = { id: String(nextId++), author: 'Ada Fake', created: tick(), body: body.body };
      issue.comments.unshift(comment);
      return json(commentView(comment), 201);
    },
    'POST /issues/:key/links': ({ issue, body }) => {
      const type = data.linkTypes.find((t) => t.id === body?.typeId);
      if (!type || !issues.has(body?.targetKey)) {
        return problem(422, 'jira_validation', { fields: { targetKey: 'Unknown issue or link type.' } });
      }
      const link = { id: String(nextId++), type: type.name, direction: 'outward', key: body.targetKey, summary: issues.get(body.targetKey).summary };
      issue.links = [...(issue.links ?? []), link];
      return json(link, 201);
    },
    'DELETE /issues/:key/links/:linkId': ({ issue, params }) => {
      if (!(issue.links ?? []).some((l) => l.id === params.linkId)) {
        return problem(404, 'jira_not_found');
      }
      issue.links = issue.links.filter((l) => l.id !== params.linkId);
      return { status: 204 };
    },
    'GET /issues/:key/attachments/:attachmentId': ({ issue, params }) => {
      const attachment = issue.attachments.find((a) => a.id === params.attachmentId);
      if (!attachment) {
        return problem(404, 'jira_not_found');
      }
      return {
        body: PIXEL,
        headers: { 'Content-Type': 'image/png', 'Cache-Control': 'private, max-age=300', 'Content-Security-Policy': 'sandbox' },
      };
    },
  };

  const removers = Object.entries(handlers).map(([spec, handler]) => {
    const [method, suffix] = spec.split(' ');
    const pattern = `/api/teams/:teamId/jira${suffix === '/' ? '' : suffix}`;
    return server.route(method, pattern, (request) => {
      calls.push({ method, route: suffix, params: request.params, body: request.body });
      if (request.params.teamId !== teamId) {
        return problem(409, 'jira_not_configured');
      }
      if (!control.config.configured) {
        return suffix === '/' ? json({ configured: false }) : problem(409, 'jira_not_configured');
      }
      if (failures.length > 0) {
        return failures.shift();
      }
      let issue = null;
      if (request.params.key !== undefined) {
        issue = issues.get(request.params.key);
        if (!issue) {
          return problem(404, 'jira_not_found');
        }
      }
      return handler({ ...request, issue });
    });
  });
  control.uninstall = () => removers.forEach((remove) => remove());
  return control;
}
