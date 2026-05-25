# Security Policy

CodexBarWin reads local Codex auth/session files for the current Windows user, so security and privacy reports matter.

Please do not open a public issue with:

- OAuth tokens, refresh tokens, or access tokens.
- `%USERPROFILE%\.codex\auth.json` contents.
- Full logs that contain account identifiers or private paths.
- Screenshots that expose private account details.

If you find a security or privacy issue, use GitHub private vulnerability reporting if it is enabled for the repository. If it is not enabled yet, open a public issue that only says you have a security report and wait for maintainer follow-up; do not include sensitive details in the issue.

When reporting a bug publicly, include the smallest safe reproduction you can. Redact tokens, account ids, email addresses, and local file paths when possible.
