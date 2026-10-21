# Retro Markup corpus

These files are the contract for the two markup implementations: `src/Retroscope.Core/Markup` (C#, U4) and
`web/src/markup/parse.js` (JS, U5). Both must turn every `input` into exactly `doc`, and both writers must turn `doc`
back into exactly `markup`. DESIGN §g.6 gives the summary; this file is the precise grammar.

## Case format

Each `*.json` file is `{"cases": [ ... ]}`. A case has:

| Field | Meaning |
|---|---|
| `name` | Unique within its file |
| `dialect` | `retro` (cards, comments, action items), `jira` (Jira comments, new issue descriptions) or `jiraDescription` (editing an existing description) |
| `input` | The markup to parse |
| `doc` | The expected Doc, in the wire shape below; compare structurally, not as text |
| `markup` | The canonical markup the writer produces for `doc`; parsing it gives `doc` again |
| `mentions` | Present when the input mentions someone: distinct user ids in document order |

## Doc wire shape

Every node is an object with a `t` kind. Containers have a `c` array of children; scalar content is `v`.
The server sends exactly this shape (`DocJson`), and renderers build DOM from it with `createElement` and
`textContent` only.

| `t` | Fields | Produced by |
|---|---|---|
| `doc` | `c`: blocks | root |
| `p` | `c`: inlines | all dialects |
| `ul` | `c`: `li` | all dialects |
| `ol` | `start`, `c`: `li` | all dialects |
| `li` | `c`: blocks | all dialects |
| `quote` | `c`: blocks | all dialects |
| `pre` | `lang` (absent when none), `v` | all dialects |
| `h` | `level` 1–6, `c`: inlines | `jira`, `jiraDescription` |
| `hr` | | `jira`, `jiraDescription` |
| `island` | `id`, `c`: blocks (empty from markup; the read-only rendering from ADF) | `jiraDescription`, top level only |
| `panel` | `kind` (`info`, `note`, `warning`, `success`, `error`), `c`: blocks | ADF only (U6) |
| `table`, `tr`, `th`, `td` | `c` | ADF only |
| `expand` | `title`, `c`: blocks | ADF only |
| `tasks`, `task` | `task` has `done`, `c`: inlines | ADF only |
| `text` | `v` | all dialects |
| `b`, `i`, `s` | `c`: inlines | all dialects |
| `u` | `c`: inlines | ADF only |
| `code` | `v` | all dialects |
| `a` | `href`, `c`: inlines | all dialects |
| `br` | | all dialects |
| `mention` | `id` (lower-case uuid), `name` | `retro` |
| `img` | `blob` (lower-case uuid), `alt` | `retro` |
| `attachment` | `id`, `alt` | ADF only |
| `status` | `v`, `color` (`neutral`, `purple`, `blue`, `red`, `yellow`, `green`) | ADF only |

## Grammar

Parsing never fails: anything that does not match a rule below is literal text. Character classes are ASCII-only
and work on UTF-16 code units, so both languages classify identically. *Whitespace* is space, tab or `\n`.
*Punctuation* is the ASCII punctuation range (`!` to `/`, `:` to `@`, `[` to `` ` ``, `{` to `~`).
*Alphanumeric* is `[A-Za-z0-9]`.

### Lines and blocks

1. `\r\n` and lone `\r` become `\n`; the text is split into lines on `\n`.
2. A line is *blank* when it holds only spaces and tabs. Blank lines separate blocks and produce nothing.
3. Blocks are recognised at the start of a line, in this order (the first match wins):
   1. **Fence**: a run of three or more backticks, optionally followed by spaces/tabs and a language of 1–32
      characters from `[A-Za-z0-9_+#.-]`, optionally followed by spaces/tabs. Any other info string makes the line
      ordinary text. The block runs to the first later line that is a run of at least as many backticks followed
      only by spaces/tabs, or to the end of its container. Content lines are verbatim, joined by `\n`.
   2. **Quote**: a line starting with `>`. Consecutive such lines form one quote. Remove `>` and one following space
      if present, then parse the remaining lines as blocks. A blank line ends the quote.
   3. **List item**: `-` followed by a space or the end of the line (bullet), or 1–9 ASCII digits, `.`, and a space or
      the end of the line (ordered; `start` is the first item's number as an integer). The marker width *w* is the
      marker length plus one. The item's first line is the text after the marker and one space. A following line
      indented by at least *w* spaces belongs to the item with *w* spaces removed. Blank lines belong to the item only
      when the next non-blank line is indented by at least *w* (they are kept, with up to *w* spaces removed). Any
      other line ends the item. The item's lines are parsed as blocks. Consecutive items of the same kind (bullet or
      ordered) form one list; a blank line between items ends the list. Later item numbers are ignored.
   4. **Heading** (`jira`, `jiraDescription`): 1–6 `#`, a space, then content that is not blank after trimming
      spaces/tabs; the content is parsed as inlines.
   5. **Rule** (`jira`, `jiraDescription`): three or more `-` followed only by spaces/tabs.
   6. **Island** (`jiraDescription`, top level only): `[[island:` + 1–64 characters from `[A-Za-z0-9_-]` + `]]`,
      followed only by spaces/tabs.
   7. **Paragraph**: otherwise. It continues over following lines until a blank line or a line that starts any block
      above. Each line is trimmed of spaces and tabs, the lines are joined with `\n`, and the result is parsed as
      inlines.
4. Quotes and list items nest at most 16 deep. At depth 16, quote and list markers are ordinary text.
5. Indentation never makes code; only fences do.

### Inlines

Inline parsing walks a *scope* (a range of the paragraph text) from left to right:

1. `\` followed by punctuation is that character as text. Any other `\` is text.
2. `\n` is a `br`.
3. **Code span**: a run of *n* backticks opens; it closes at the next run of exactly *n* backticks on the same line.
   If the content is at least two characters long, starts and ends with a space and is not all spaces, one space is
   removed from each end. No escapes apply inside. Without a closer, the whole backtick run is text.
4. **Mention** (`retro`, not inside a link label): `[[mention:` + a canonical uuid (8-4-4-4-12 hex, either case) +
   `:` + a name of 1–100 characters containing no `]` or newline + `]]`.
5. **Image** (`retro`, not inside a link label): `![` + an alt of 0–100 characters containing no `]` or newline +
   `](blob:` + a canonical uuid + `)`.
6. **Link** (not inside a link label): `[`, a label running to the first `]` that is not escaped (no newline; at
   least one character), then `(`, an href of 1–2048 characters greater than U+0020 other than U+007F, `(`, `)`, `<`
   and `>`, then `)`. The href must start (ignoring case) with `http://`, `https://` or `mailto:` and have at least one
   character after it. The label is parsed as inlines with mentions, images and links disabled.
7. **Emphasis** (at most 16 deep): the opener must be followed by a non-whitespace character inside the scope.
   * `**` opens bold, `~~` opens strike.
   * `_` opens italic unless the character before it inside the scope is alphanumeric.
   * `*` that does not open bold (no `**` here, or no bold closer) opens italic.
   * An emphasis never opens inside an emphasis with the same delimiter (bold in bold, strike in strike, `*` italic in
     `*` italic, `_` italic in `_` italic). This restriction resets inside link labels.

   **Closer search** starts right after the opener and scans to the scope end. Escapes, code spans and (when they
   would parse here) mentions, images and links are skipped whole. At a run of the delimiter character (counted from
   the scan position):
   * `**` / `~~`: if the run has at least 2 characters, the candidate is its last two;
   * `*`: if the run has odd length, the candidate is its last character;
   * `_`: if the run has length 1, the candidate is that character, and the character after it (inside the scope) must
     not be alphanumeric.

   A candidate is valid when it leaves non-empty content and the character before it is not whitespace. Otherwise the
   scan continues after the run. The first valid candidate closes; the content between is parsed as a new scope. With
   no closer, the opener's first character is text and parsing continues with the next character.
8. Everything else is text; adjacent text merges into one `text` node.

### Writer

The writer is canonical: blocks are separated by a blank line, except that a list directly follows a paragraph on the
next line. List items use `- ` or `N. ` (numbered from `start`). Nested lines are indented by the marker width and blank
lines stay empty. Quote lines are prefixed with `> ` (`>` for an empty line). Fences use three backticks, or one more
than the longest backtick run that starts a content line. Text escapes ``\ * _ ~ ` [ ]``. A paragraph line that would
start a block gets a `\` before its first character (before the `.` for an ordered marker). A text `!` directly before
a `[` is escaped. Italic uses `_`, and `*` when `_` cannot be a delimiter there (see `InlineWriter`).
